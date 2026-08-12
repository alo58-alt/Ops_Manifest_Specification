using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CompanyOps.Agent.Deployment;

public sealed record DeploymentActivationRequest(
    string ProjectId,
    string Environment,
    string ReleasePath,
    JsonObject ProjectManifest,
    JsonObject ReleaseManifest,
    JsonObject Binding);

public sealed record DeploymentActivationResult(
    bool Success,
    string? Detail = null,
    IReadOnlyList<string>? Steps = null,
    IDeploymentActivationRollback? Rollback = null);

public interface IDeploymentActivationRollback
{
    Task<DeploymentActivationResult> RestoreAsync(CancellationToken cancellationToken);
}

public interface IDeploymentActivator
{
    Task<DeploymentActivationResult> PlanAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken);

    Task<DeploymentActivationResult> ActivateAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken);
}

public sealed record DeploymentEntrypointTarget(
    string ProjectId,
    string Environment,
    string ComponentId,
    string Kind,
    string NativeName,
    string ExecutablePath,
    string BinaryPath,
    string? WorkingDirectory);

public sealed record DeploymentEntrypointSnapshot(
    string ComponentId,
    string Kind,
    string NativeName,
    string BinaryPath,
    bool WasRunning);

public sealed record DeploymentEntrypointCaptureResult(
    bool Success,
    DeploymentEntrypointSnapshot? Snapshot = null,
    string? Detail = null);

public interface IDeploymentEntrypointAdapter
{
    string Kind { get; }

    Task<DeploymentEntrypointCaptureResult> CaptureAsync(
        DeploymentEntrypointTarget target,
        CancellationToken cancellationToken);

    Task<AdapterExecutionResult> ApplyAsync(
        DeploymentEntrypointTarget target,
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<AdapterExecutionResult> RestoreAsync(
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed class NativeDeploymentActivator : IDeploymentActivator
{
    private static readonly Regex PlaceholderPattern = new(
        "\\$\\{(?<name>[A-Z][A-Z0-9_]*)\\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, IDeploymentEntrypointAdapter> _entrypointAdapters;
    private readonly IReadOnlyDictionary<string, IComponentControlAdapter> _controlAdapters;
    private readonly IManifestHealthGate _healthGate;

    public NativeDeploymentActivator(
        IEnumerable<IDeploymentEntrypointAdapter> entrypointAdapters,
        IEnumerable<IComponentControlAdapter> controlAdapters,
        IManifestHealthGate healthGate)
    {
        _entrypointAdapters = entrypointAdapters.ToDictionary(static item => item.Kind, StringComparer.Ordinal);
        _controlAdapters = controlAdapters.ToDictionary(static item => item.Kind, StringComparer.Ordinal);
        _healthGate = healthGate;
    }

    public Task<DeploymentActivationResult> PlanAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = BuildPlan(request, requirePayloadsOnDisk: false);
        return Task.FromResult(plan.Error is null
            ? new DeploymentActivationResult(
                true,
                $"{plan.Items.Count} 个原生组件具备受控激活能力",
                plan.Items.Select(static item => $"计划切换 {item.ComponentId} ({item.Kind})").ToArray())
            : new DeploymentActivationResult(false, plan.Error.Value.Detail));
    }

    public async Task<DeploymentActivationResult> ActivateAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken)
    {
        var plan = BuildPlan(request, requirePayloadsOnDisk: true);
        if (plan.Error is not null)
        {
            return new DeploymentActivationResult(false, plan.Error.Value.Detail);
        }

        var steps = new List<string>();
        var captures = new Dictionary<string, DeploymentEntrypointSnapshot>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = await item.EntrypointAdapter.CaptureAsync(item.Target, cancellationToken);
            if (!capture.Success || capture.Snapshot is null)
            {
                return new DeploymentActivationResult(
                    false,
                    $"组件 {item.ComponentId} 激活预检失败：{capture.Detail ?? "无法读取当前入口"}",
                    steps);
            }

            captures.Add(item.ComponentId, capture.Snapshot);
            steps.Add($"组件 {item.ComponentId} 已读取当前原生入口和运行状态");
        }

        var rollback = new NativeActivationRollback(
            plan.Items,
            captures,
            request.ProjectManifest,
            request.Binding,
            _healthGate);

        try
        {
            foreach (var item in plan.Items.Reverse())
            {
                var stopped = await item.ControlAdapter.ExecuteAsync(
                    item.ControlTarget,
                    ComponentOperationAction.Stop,
                    cancellationToken);
                steps.Add($"停止 {item.ComponentId}：{stopped.Detail}");
                if (!stopped.Success)
                {
                    var restored = await rollback.RestoreAsync(cancellationToken);
                    return ActivationFailure(item.ComponentId, "停止失败", stopped.Detail, restored, steps);
                }
            }

            foreach (var item in plan.Items)
            {
                var applied = await item.EntrypointAdapter.ApplyAsync(
                    item.Target,
                    captures[item.ComponentId],
                    cancellationToken);
                steps.Add($"切换 {item.ComponentId} 原生入口：{applied.Detail}");
                if (!applied.Success)
                {
                    var restored = await rollback.RestoreAsync(cancellationToken);
                    return ActivationFailure(item.ComponentId, "入口切换失败", applied.Detail, restored, steps);
                }
            }

            var healthTimeout = TimeSpan.FromSeconds(Math.Clamp(
                request.ProjectManifest["update"]?["healthTimeoutSeconds"]?.GetValue<int>() ?? 60,
                5,
                600));
            foreach (var item in plan.Items)
            {
                var started = await item.ControlAdapter.ExecuteAsync(
                    item.ControlTarget,
                    ComponentOperationAction.Start,
                    cancellationToken);
                steps.Add($"启动 {item.ComponentId}：{started.Detail}");
                if (!started.Success)
                {
                    var restored = await rollback.RestoreAsync(cancellationToken);
                    return ActivationFailure(item.ComponentId, "启动失败", started.Detail, restored, steps);
                }

                var health = await WaitForHealthAsync(
                    request.ProjectManifest,
                    request.Binding,
                    item.ComponentId,
                    healthTimeout,
                    cancellationToken);
                steps.Add($"健康复核 {item.ComponentId}：{health.Detail}");
                if (!health.Success)
                {
                    var restored = await rollback.RestoreAsync(cancellationToken);
                    return ActivationFailure(item.ComponentId, "健康复核失败", health.Detail, restored, steps);
                }
            }

            return new DeploymentActivationResult(
                true,
                "原生入口切换、依赖启动和健康复核全部通过",
                steps,
                rollback);
        }
        catch (OperationCanceledException)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var restored = await rollback.RestoreAsync(cleanupTimeout.Token);
            return new DeploymentActivationResult(
                false,
                $"激活已取消；旧入口恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}",
                steps);
        }
    }

    private ActivationPlan BuildPlan(
        DeploymentActivationRequest request,
        bool requirePayloadsOnDisk)
    {
        if (!Directory.Exists(request.ReleasePath) && requirePayloadsOnDisk)
        {
            return ActivationPlan.Fail("release_missing", "不可变 release 目录不存在");
        }

        var components = request.ProjectManifest["components"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var payloads = request.ReleaseManifest["componentPayloads"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var bindings = request.Binding["componentBindings"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        if (components.Length == 0)
        {
            return ActivationPlan.Fail("components_missing", "ProjectManifest 没有可激活组件");
        }

        if (payloads.Length != components.Length)
        {
            return ActivationPlan.Fail("payload_coverage_invalid", "ReleaseManifest 必须为每个项目组件提供且只提供一个 componentPayload");
        }

        var componentMap = UniqueBy(components, "id");
        var payloadMap = UniqueBy(payloads, "componentId");
        var bindingMap = UniqueBy(bindings, "componentId");
        if (componentMap is null || payloadMap is null || bindingMap is null ||
            componentMap.Count != components.Length || payloadMap.Count != components.Length)
        {
            return ActivationPlan.Fail("activation_mapping_not_unique", "组件、入口载荷或主机绑定不唯一");
        }

        if (!componentMap.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(payloadMap.Keys))
        {
            return ActivationPlan.Fail("payload_coverage_invalid", "componentPayload 与 ProjectManifest 组件集合不一致");
        }

        var order = TopologicalOrder(componentMap);
        if (order.Error is not null)
        {
            return ActivationPlan.Fail("dependency_invalid", order.Error);
        }

        var portValues = BuildPortPlaceholders(request.Binding);
        var items = new List<ActivationItem>();
        foreach (var componentId in order.ComponentIds)
        {
            var component = componentMap[componentId];
            var payload = payloadMap[componentId];
            if (!bindingMap.TryGetValue(componentId, out var binding))
            {
                return ActivationPlan.Fail("component_binding_missing", $"组件 {componentId} 缺少唯一主机绑定");
            }

            var kind = component["kind"]?.GetValue<string>() ?? string.Empty;
            if (!_entrypointAdapters.TryGetValue(kind, out var entrypointAdapter) ||
                !_controlAdapters.TryGetValue(kind, out var controlAdapter))
            {
                return ActivationPlan.Fail(
                    "activation_adapter_missing",
                    $"组件 {componentId} 类型 {kind} 尚无生产激活适配器，拒绝把解包当作部署成功");
            }

            var entrypoint = component["entrypoint"]?.GetValue<string>();
            if (!string.Equals(entrypoint, payload["entrypoint"]?.GetValue<string>(), StringComparison.Ordinal))
            {
                return ActivationPlan.Fail("entrypoint_mismatch", $"组件 {componentId} 的声明入口与发布入口不一致");
            }

            var artifactId = payload["artifactId"]?.GetValue<string>() ?? string.Empty;
            var artifactRoot = Path.GetFullPath(Path.Combine(request.ReleasePath, artifactId));
            var executablePath = ResolveUnderRoot(artifactRoot, payload["path"]?.GetValue<string>());
            if (executablePath is null)
            {
                return ActivationPlan.Fail("unsafe_entrypoint_path", $"组件 {componentId} 的发布入口路径不安全");
            }

            if (requirePayloadsOnDisk && !File.Exists(executablePath))
            {
                return ActivationPlan.Fail("entrypoint_missing", $"组件 {componentId} 的发布入口文件不存在");
            }

            string? workingDirectory = null;
            if (payload["workingDirectory"]?.GetValue<string>() is { } relativeWorkingDirectory)
            {
                workingDirectory = ResolveUnderRoot(artifactRoot, relativeWorkingDirectory);
                if (workingDirectory is null || requirePayloadsOnDisk && !Directory.Exists(workingDirectory))
                {
                    return ActivationPlan.Fail("working_directory_invalid", $"组件 {componentId} 的工作目录不存在或路径不安全");
                }

                if (kind == "windowsService" &&
                    !string.Equals(
                        Path.TrimEndingDirectorySeparator(workingDirectory),
                        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(executablePath)!),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ActivationPlan.Fail(
                        "working_directory_unsupported",
                        $"Windows Service {componentId} 不能安全设置独立工作目录；workingDirectory 只能等于入口文件目录");
                }
            }

            var arguments = new List<string>();
            foreach (var argumentNode in payload["arguments"]?.AsArray() ?? [])
            {
                var resolved = ResolveArgument(argumentNode!.GetValue<string>(), portValues);
                if (resolved is null)
                {
                    return ActivationPlan.Fail(
                        "argument_placeholder_unresolved",
                        $"组件 {componentId} 启动参数包含未知或未绑定占位符");
                }

                arguments.Add(resolved);
            }

            var nativeName = binding["nativeName"]?.GetValue<string>() ?? string.Empty;
            var binaryPath = WindowsCommandLine.Build(executablePath, arguments);
            var target = new DeploymentEntrypointTarget(
                request.ProjectId,
                request.Environment,
                componentId,
                kind,
                nativeName,
                executablePath,
                binaryPath,
                workingDirectory);
            items.Add(new ActivationItem(
                componentId,
                kind,
                target,
                entrypointAdapter,
                controlAdapter,
                new ComponentControlTarget(
                    request.ProjectId,
                    request.Environment,
                    componentId,
                    kind,
                    nativeName,
                    null)));
        }

        return new ActivationPlan(items, null);
    }

    private async Task<HealthGateResult> WaitForHealthAsync(
        JsonObject projectManifest,
        JsonObject binding,
        string componentId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        HealthGateResult? last = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _healthGate.ProbeAsync(projectManifest, binding, componentId, cancellationToken);
            if (last.Success)
            {
                return last;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return new HealthGateResult(false, $"健康超时：{last?.Detail ?? "没有探针结果"}");
    }

    private static DeploymentActivationResult ActivationFailure(
        string componentId,
        string stage,
        string? detail,
        DeploymentActivationResult restored,
        List<string> steps)
    {
        steps.Add($"失败恢复：{restored.Detail}");
        return new DeploymentActivationResult(
            false,
            $"组件 {componentId} {stage}：{detail ?? "未提供详情"}；失败恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}",
            steps);
    }

    private static Dictionary<string, JsonObject>? UniqueBy(IEnumerable<JsonObject> items, string property)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = item[property]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, item))
            {
                return null;
            }
        }

        return result;
    }

    private static TopologicalResult TopologicalOrder(IReadOnlyDictionary<string, JsonObject> components)
    {
        var ordered = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string componentId)
        {
            if (visited.Contains(componentId))
            {
                return true;
            }

            if (!components.TryGetValue(componentId, out var component) || !visiting.Add(componentId))
            {
                return false;
            }

            foreach (var dependency in component["dependsOn"]?.AsArray().Select(static item => item!.GetValue<string>()) ?? [])
            {
                if (!Visit(dependency))
                {
                    return false;
                }
            }

            visiting.Remove(componentId);
            visited.Add(componentId);
            ordered.Add(componentId);
            return true;
        }

        return components.Keys.Order(StringComparer.Ordinal).All(Visit)
            ? new TopologicalResult(ordered, null)
            : new TopologicalResult([], "组件依赖缺失或形成环");
    }

    private static IReadOnlyDictionary<string, string> BuildPortPlaceholders(JsonObject binding)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in binding["portBindings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var portId = item["portId"]?.GetValue<string>();
            var port = item["port"]?.GetValue<int>();
            if (portId is null || port is null)
            {
                continue;
            }

            var token = "PORT_" + portId.ToUpperInvariant().Replace('-', '_');
            result[token] = port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static string? ResolveArgument(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        var unresolved = false;
        var result = PlaceholderPattern.Replace(
            value,
            match =>
            {
                if (placeholders.TryGetValue(match.Groups["name"].Value, out var replacement))
                {
                    return replacement;
                }

                unresolved = true;
                return match.Value;
            });
        return unresolved || result.Contains("${", StringComparison.Ordinal) ? null : result;
    }

    private static string? ResolveUnderRoot(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var resolvedRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(
            resolvedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(resolvedRoot) + Path.DirectorySeparatorChar;
        return resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? resolved : null;
    }

    private sealed record ActivationItem(
        string ComponentId,
        string Kind,
        DeploymentEntrypointTarget Target,
        IDeploymentEntrypointAdapter EntrypointAdapter,
        IComponentControlAdapter ControlAdapter,
        ComponentControlTarget ControlTarget);

    private sealed record ActivationPlan(
        IReadOnlyList<ActivationItem> Items,
        (string Code, string Detail)? Error)
    {
        public static ActivationPlan Fail(string code, string detail) => new([], (code, detail));
    }

    private sealed record TopologicalResult(IReadOnlyList<string> ComponentIds, string? Error);

    private sealed class NativeActivationRollback(
        IReadOnlyList<ActivationItem> items,
        IReadOnlyDictionary<string, DeploymentEntrypointSnapshot> captures,
        JsonObject projectManifest,
        JsonObject binding,
        IManifestHealthGate healthGate) : IDeploymentActivationRollback
    {
        private readonly object _sync = new();
        private Task<DeploymentActivationResult>? _restoreTask;

        public Task<DeploymentActivationResult> RestoreAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            lock (_sync)
            {
                _restoreTask ??= RestoreWithTimeoutAsync();
                return _restoreTask;
            }
        }

        private async Task<DeploymentActivationResult> RestoreWithTimeoutAsync()
        {
            var timeoutSeconds = Math.Clamp(items.Count * 90, 60, 600);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            return await RestoreCoreAsync(timeout.Token);
        }

        private async Task<DeploymentActivationResult> RestoreCoreAsync(CancellationToken cancellationToken)
        {
            var steps = new List<string>();
            var success = true;
            foreach (var item in items.Reverse())
            {
                var stopped = await item.ControlAdapter.ExecuteAsync(
                    item.ControlTarget,
                    ComponentOperationAction.Stop,
                    cancellationToken);
                success &= stopped.Success;
                steps.Add($"恢复前停止 {item.ComponentId}：{stopped.Detail}");
            }

            foreach (var item in items.Reverse())
            {
                var restored = await item.EntrypointAdapter.RestoreAsync(captures[item.ComponentId], cancellationToken);
                success &= restored.Success;
                steps.Add($"恢复 {item.ComponentId} 旧入口：{restored.Detail}");
            }

            foreach (var item in items.Where(item => captures[item.ComponentId].WasRunning))
            {
                var started = await item.ControlAdapter.ExecuteAsync(
                    item.ControlTarget,
                    ComponentOperationAction.Start,
                    cancellationToken);
                success &= started.Success;
                steps.Add($"恢复启动 {item.ComponentId}：{started.Detail}");
                if (started.Success)
                {
                    var health = await WaitForRestoreHealthAsync(item.ComponentId, cancellationToken);
                    success &= health.Success;
                    steps.Add($"恢复健康 {item.ComponentId}：{health.Detail}");
                }
            }

            return new DeploymentActivationResult(
                success,
                success ? "旧入口和原运行状态已恢复" : "旧入口恢复存在失败，必须人工处置",
                steps);
        }

        private async Task<HealthGateResult> WaitForRestoreHealthAsync(
            string componentId,
            CancellationToken cancellationToken)
        {
            var timeoutSeconds = Math.Clamp(
                projectManifest["update"]?["healthTimeoutSeconds"]?.GetValue<int>() ?? 60,
                5,
                600);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            HealthGateResult? last = null;
            do
            {
                last = await healthGate.ProbeAsync(projectManifest, binding, componentId, cancellationToken);
                if (last.Success)
                {
                    return last;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
            while (DateTimeOffset.UtcNow < deadline);

            return new HealthGateResult(false, $"恢复健康超时：{last?.Detail ?? "没有探针结果"}");
        }
    }
}

public sealed class WindowsServiceDeploymentEntrypointAdapter : IDeploymentEntrypointAdapter
{
    public string Kind => "windowsService";

    public Task<DeploymentEntrypointCaptureResult> CaptureAsync(
        DeploymentEntrypointTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !SafeNativeName(target.NativeName))
        {
            return Task.FromResult(new DeploymentEntrypointCaptureResult(false, Detail: "SCM 目标无效或当前不是 Windows"));
        }

        var matches = System.ServiceProcess.ServiceController.GetServices()
            .Where(service => string.Equals(service.ServiceName, target.NativeName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            foreach (var match in matches)
            {
                match.Dispose();
            }

            return Task.FromResult(new DeploymentEntrypointCaptureResult(
                false,
                Detail: $"SCM 精确名称匹配数量为 {matches.Length}"));
        }

        using var service = matches[0];
        service.Refresh();
        if (service.Status is not (System.ServiceProcess.ServiceControllerStatus.Running or
            System.ServiceProcess.ServiceControllerStatus.Stopped))
        {
            return Task.FromResult(new DeploymentEntrypointCaptureResult(
                false,
                Detail: $"SCM 当前状态 {service.Status} 不允许入口迁移"));
        }

        try
        {
            var binaryPath = WindowsServiceConfiguration.QueryBinaryPath(target.NativeName);
            return Task.FromResult(new DeploymentEntrypointCaptureResult(
                true,
                new DeploymentEntrypointSnapshot(
                    target.ComponentId,
                    target.Kind,
                    target.NativeName,
                    binaryPath,
                    service.Status == System.ServiceProcess.ServiceControllerStatus.Running),
                "SCM 当前入口读取成功"));
        }
        catch (Win32Exception exception)
        {
            return Task.FromResult(new DeploymentEntrypointCaptureResult(false, Detail: exception.Message));
        }
    }

    public Task<AdapterExecutionResult> ApplyAsync(
        DeploymentEntrypointTarget target,
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(target.NativeName, snapshot.NativeName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target.ExecutablePath))
        {
            return Task.FromResult(new AdapterExecutionResult(false, "SCM 目标变化或新入口文件不存在"));
        }

        return ChangeAsync(target.NativeName, target.BinaryPath, "SCM 新入口已写入");
    }

    public Task<AdapterExecutionResult> RestoreAsync(
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ChangeAsync(snapshot.NativeName, snapshot.BinaryPath, "SCM 旧入口已恢复");
    }

    private static Task<AdapterExecutionResult> ChangeAsync(string serviceName, string binaryPath, string detail)
    {
        try
        {
            WindowsServiceConfiguration.ChangeBinaryPath(serviceName, binaryPath);
            var actual = WindowsServiceConfiguration.QueryBinaryPath(serviceName);
            return Task.FromResult(string.Equals(actual, binaryPath, StringComparison.Ordinal)
                ? new AdapterExecutionResult(true, detail)
                : new AdapterExecutionResult(false, "SCM 写入后回读入口不一致"));
        }
        catch (Win32Exception exception)
        {
            return Task.FromResult(new AdapterExecutionResult(false, exception.Message));
        }
    }

    private static bool SafeNativeName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(static character => !char.IsControl(character));
}

internal static class WindowsCommandLine
{
    public static string Build(string executablePath, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Quote(executablePath) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length + 2).Append('"');
        var slashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashCount * 2 + 1).Append('"');
                slashCount = 0;
                continue;
            }

            result.Append('\\', slashCount).Append(character);
            slashCount = 0;
        }

        return result.Append('\\', slashCount * 2).Append('"').ToString();
    }
}

internal static class WindowsServiceConfiguration
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xffffffff;
    private const int ErrorInsufficientBuffer = 122;

    public static string QueryBinaryPath(string serviceName)
    {
        using var service = Open(serviceName, ServiceQueryConfig);
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, $"读取服务 {serviceName} 配置所需缓冲区失败");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取服务 {serviceName} 配置失败");
            }

            var config = Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
            return Marshal.PtrToStringUni(config.BinaryPathName)
                ?? throw new Win32Exception($"服务 {serviceName} 的入口为空");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void ChangeBinaryPath(string serviceName, string binaryPath)
    {
        using var service = Open(serviceName, ServiceChangeConfig | ServiceQueryConfig);
        if (!ChangeServiceConfig(
                service,
                ServiceNoChange,
                ServiceNoChange,
                ServiceNoChange,
                binaryPath,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"修改服务 {serviceName} 入口失败");
        }
    }

    private static SafeServiceHandle Open(string serviceName, uint access)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "连接 Windows SCM 失败");
        }

        var service = OpenService(manager, serviceName, access);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new Win32Exception(error, $"打开服务 {serviceName} 失败");
        }

        return service;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigNative
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
