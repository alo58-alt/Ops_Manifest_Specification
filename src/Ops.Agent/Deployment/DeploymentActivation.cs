using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CompanyOps.Agent.Deployment;

public sealed record DeploymentActivationRequest(
    string ProjectId,
    string Environment,
    string ReleasePath,
    JsonObject ProjectManifest,
    JsonObject ReleaseManifest,
    JsonObject Binding,
    bool AllowCreate = false);

public sealed record WindowsServiceInstallSpec(
    string ServiceName,
    string DisplayName,
    string BinaryPath,
    string StartMode,
    string ServiceAccountRef,
    string ServiceAccountName,
    IReadOnlyList<string> Dependencies,
    int FailureRestartLimit,
    string? OwnershipMarker = null);

public sealed record DeploymentActivationResult(
    bool Success,
    string? Detail = null,
    IReadOnlyList<string>? Steps = null,
    IDeploymentActivationRollback? Rollback = null,
    IReadOnlyDictionary<string, string>? NativeIds = null);

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
    string? WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string? SnapshotFileName = null,
    string? ControlPipeName = null,
    string? OwnerSid = null,
    int SnapshotMaxAgeSeconds = 30,
    string? InstallRoot = null,
    IReadOnlyDictionary<string, string>? ArgumentValues = null,
    string? LegacyCwd = null,
    string? LegacyScript = null,
    IReadOnlyList<string>? LegacyArguments = null,
    bool AllowCreate = false,
    WindowsServiceInstallSpec? WindowsService = null);

public sealed record DeploymentEntrypointSnapshot(
    string ComponentId,
    string Kind,
    string NativeName,
    string BinaryPath,
    bool WasRunning,
    string? ProjectId = null,
    string? Environment = null,
    string? ExecutablePath = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Arguments = null,
    bool HadManagedState = false,
    string? HostingAdapter = null,
    string? HostApplication = null,
    string? HostWorkingDirectory = null,
    string? HostArguments = null,
    int? PmId = null,
    string? ControlPipeName = null,
    bool Existed = true,
    WindowsServiceInstallSpec? WindowsService = null);

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

    public async Task<DeploymentActivationResult> PlanAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = BuildPlan(request, requirePayloadsOnDisk: false);
        if (plan.Error is not null)
        {
            return new DeploymentActivationResult(false, plan.Error.Value.Detail);
        }

        var steps = new List<string>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeploymentEntrypointCaptureResult capture;
            try
            {
                capture = await item.EntrypointAdapter.CaptureAsync(item.Target, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new DeploymentActivationResult(
                    false,
                    $"组件 {item.ComponentId} 只读预检异常 {exception.GetType().Name}：{exception.Message}",
                    steps);
            }

            if (!capture.Success || capture.Snapshot is null)
            {
                return new DeploymentActivationResult(
                    false,
                    $"组件 {item.ComponentId} 只读预检失败：{capture.Detail ?? "无法读取当前入口"}",
                    steps);
            }

            steps.Add($"组件 {item.ComponentId} 当前入口状态可读取：{capture.Detail}");
        }

        return new DeploymentActivationResult(
            true,
            $"{plan.Items.Count} 个组件已通过只读预检并具备受控激活能力",
            steps);
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
        var controlTargets = new Dictionary<string, ComponentControlTarget>(StringComparer.Ordinal);
        var attemptedNewServices = new HashSet<string>(StringComparer.Ordinal);
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
            controlTargets.Add(item.ComponentId, ControlTargetForSnapshot(item, capture.Snapshot));
            steps.Add($"组件 {item.ComponentId} 已读取当前原生入口和运行状态");
        }

        var rollback = new NativeActivationRollback(
            plan.Items,
            captures,
            request.ProjectManifest,
            request.Binding,
            _healthGate,
            controlTargets,
            attemptedNewServices);

        try
        {
            foreach (var item in plan.Items.Reverse())
            {
                if (item.Kind == "windowsService" && !captures[item.ComponentId].Existed)
                {
                    steps.Add($"停止 {item.ComponentId}：首次 Install，无旧 Windows Service");
                    continue;
                }
                if (item.Kind == "pm2Legacy" && controlTargets[item.ComponentId].PmId is null)
                {
                    steps.Add($"停止 {item.ComponentId}：首次登记，无旧 PM2 实例");
                    continue;
                }
                var stopped = await item.ControlAdapter.ExecuteAsync(
                    controlTargets[item.ComponentId],
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
                if (item.Kind == "pm2Legacy")
                {
                    steps.Add($"切换 {item.ComponentId} 原生入口：将在依赖拓扑启动阶段精确登记");
                    continue;
                }
                if (item.Kind == "windowsService" && !captures[item.ComponentId].Existed)
                {
                    // Register the attempt immediately before the adapter call. If SCM creation
                    // partially succeeds, rollback may use the random ownership marker captured
                    // for this operation; components not yet attempted are never eligible.
                    attemptedNewServices.Add(item.ComponentId);
                }
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
                AdapterExecutionResult started;
                if (item.Kind == "pm2Legacy")
                {
                    started = await item.EntrypointAdapter.ApplyAsync(
                        item.Target,
                        captures[item.ComponentId],
                        cancellationToken);
                    if (started.Success && started.PmId is >= 0)
                    {
                        controlTargets[item.ComponentId] = item.ControlTarget with { PmId = started.PmId };
                    }
                    else if (started.Success)
                    {
                        started = new AdapterExecutionResult(false, "PM2 登记未返回精确 pm_id");
                    }
                }
                else
                {
                    started = await item.ControlAdapter.ExecuteAsync(
                        controlTargets[item.ComponentId],
                        ComponentOperationAction.Start,
                        cancellationToken);
                }
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
                rollback,
                controlTargets
                    .Where(static pair => pair.Value.PmId is not null)
                    .ToDictionary(static pair => pair.Key, static pair => $"pm_id:{pair.Value.PmId}", StringComparer.Ordinal));
        }
        catch (OperationCanceledException)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var restored = await RestoreAfterUnhandledFailureAsync(rollback, cleanupTimeout.Token);
            return new DeploymentActivationResult(
                false,
                $"激活已取消；旧入口恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}",
                steps);
        }
        catch (Exception exception)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var restored = await RestoreAfterUnhandledFailureAsync(rollback, cleanupTimeout.Token);
            return new DeploymentActivationResult(
                false,
                $"激活异常 {exception.GetType().Name}：{exception.Message}；旧入口恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}",
                steps);
        }
    }

    private static ComponentControlTarget ControlTargetForSnapshot(
        ActivationItem item,
        DeploymentEntrypointSnapshot snapshot) =>
        item.Kind == "pm2Legacy"
            ? item.ControlTarget with
            {
                PmId = snapshot.PmId,
                ExpectedCwd = snapshot.WorkingDirectory,
                ExpectedScript = snapshot.ExecutablePath,
                ExpectedArguments = snapshot.Arguments,
                ControlPipeName = snapshot.ControlPipeName
            }
            : item.ControlTarget;

    private static async Task<DeploymentActivationResult> RestoreAfterUnhandledFailureAsync(
        IDeploymentActivationRollback rollback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await rollback.RestoreAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return new DeploymentActivationResult(
                false,
                $"恢复过程异常 {exception.GetType().Name}：{exception.Message}");
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

        var argumentValues = BuildArgumentPlaceholders(request.Binding);
        var installRoot = request.Binding["roots"]?["install"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot))
            return ActivationPlan.Fail("install_root_invalid", "EnvironmentBinding 缺少有效的安装根目录");
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
                var resolved = ResolveArgument(argumentNode!.GetValue<string>(), argumentValues);
                if (resolved is null)
                {
                    return ActivationPlan.Fail(
                        "argument_placeholder_unresolved",
                        $"组件 {componentId} 启动参数包含未知或未绑定占位符");
                }

                arguments.Add(resolved);
            }

            if (kind == "pm2Legacy")
            {
                var pm2 = payload["pm2"] as JsonObject;
                var declaredPm2 = component["pm2"] as JsonObject;
                if (pm2 is null || declaredPm2 is null ||
                    !string.Equals(pm2["name"]?.GetValue<string>(), declaredPm2["name"]?.GetValue<string>(), StringComparison.Ordinal) ||
                    !string.Equals(pm2["script"]?.GetValue<string>(), payload["path"]?.GetValue<string>(), StringComparison.Ordinal) ||
                    !string.Equals(pm2["cwd"]?.GetValue<string>(), payload["workingDirectory"]?.GetValue<string>(), StringComparison.Ordinal) ||
                    !JsonArrayValuesEqual(pm2["arguments"] as JsonArray, payload["arguments"] as JsonArray))
                {
                    return ActivationPlan.Fail("pm2_payload_identity_invalid", $"组件 {componentId} 的 PM2 发布身份与声明或通用载荷不一致");
                }
            }

            var nativeName = binding["nativeName"]?.GetValue<string>() ?? string.Empty;
            if (kind == "pm2Legacy" &&
                !string.Equals(nativeName, component["pm2"]?["name"]?.GetValue<string>(), StringComparison.Ordinal))
            {
                return ActivationPlan.Fail("pm2_binding_identity_invalid", $"组件 {componentId} 的 PM2 nativeName 与项目声明不一致");
            }
            var binaryPath = WindowsCommandLine.Build(executablePath, arguments);
            WindowsServiceInstallSpec? windowsService = null;
            if (kind == "windowsService")
            {
                var service = component["service"] as JsonObject;
                var accountRef = binding["serviceAccountRef"]?.GetValue<string>() ?? string.Empty;
                var serviceDependencyIds = component["dependsOn"]?.AsArray()
                    .Select(static node => node?.GetValue<string>() ?? string.Empty)
                    .Where(dependencyId => componentMap.TryGetValue(dependencyId, out var dependency) &&
                                           dependency["kind"]?.GetValue<string>() == "windowsService")
                    .ToArray() ?? [];
                var dependencyNames = new List<string>(serviceDependencyIds.Length);
                foreach (var dependencyId in serviceDependencyIds)
                {
                    if (!bindingMap.TryGetValue(dependencyId, out var dependencyBinding))
                    {
                        return ActivationPlan.Fail(
                            "windows_service_dependency_binding_missing",
                            $"组件 {componentId} 的 Windows Service 依赖 {dependencyId} 缺少环境绑定");
                    }
                    dependencyNames.Add(dependencyBinding["nativeName"]?.GetValue<string>() ?? string.Empty);
                }
                windowsService = new WindowsServiceInstallSpec(
                    nativeName,
                    component["displayName"]?.GetValue<string>() ?? componentId,
                    binaryPath,
                    service?["startMode"]?.GetValue<string>() ?? "manual",
                    accountRef,
                    WindowsServiceAccount.ResolveBuiltIn(accountRef) ?? string.Empty,
                    dependencyNames,
                    service?["failureRestartLimit"]?.GetValue<int>() ?? 3);
            }
            var legacyPm2 = request.Binding["legacyPm2"] as JsonObject;
            var declaredLegacyCwd = kind == "pm2Legacy"
                ? ResolveUnderRoot(installRoot, component["pm2"]?["cwd"]?.GetValue<string>())
                : null;
            var declaredLegacyScript = kind == "pm2Legacy"
                ? ResolveUnderRoot(installRoot, component["pm2"]?["script"]?.GetValue<string>())
                : null;
            var declaredLegacyArguments = new List<string>();
            if (kind == "pm2Legacy")
            {
                foreach (var node in component["pm2"]?["arguments"]?.AsArray() ?? [])
                {
                    var resolved = ResolveArgument(node!.GetValue<string>(), argumentValues);
                    if (resolved is null)
                    {
                        return ActivationPlan.Fail("argument_placeholder_unresolved", $"组件 {componentId} 的旧 PM2 参数包含未知占位符");
                    }
                    declaredLegacyArguments.Add(resolved);
                }
                if (declaredLegacyCwd is null || declaredLegacyScript is null)
                {
                    return ActivationPlan.Fail("pm2_legacy_identity_invalid", $"组件 {componentId} 的旧 PM2 声明路径不安全");
                }
            }
            var target = new DeploymentEntrypointTarget(
                request.ProjectId,
                request.Environment,
                componentId,
                kind,
                nativeName,
                executablePath,
                binaryPath,
                workingDirectory,
                arguments,
                kind == "pm2Legacy" ? legacyPm2?["snapshotFileName"]?.GetValue<string>() : null,
                kind == "pm2Legacy" ? legacyPm2?["controlPipeName"]?.GetValue<string>() : null,
                kind == "pm2Legacy" ? legacyPm2?["ownerSid"]?.GetValue<string>() : null,
                kind == "pm2Legacy" ? legacyPm2?["maxAgeSeconds"]?.GetValue<int>() ?? 30 : 30,
                installRoot,
                argumentValues,
                declaredLegacyCwd,
                declaredLegacyScript,
                declaredLegacyArguments,
                request.AllowCreate,
                windowsService);
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
                    installRoot,
                    null,
                    kind == "pm2Legacy" ? workingDirectory : null,
                    kind == "pm2Legacy" ? executablePath : null,
                    kind == "pm2Legacy" ? arguments : null,
                    kind == "pm2Legacy" ? legacyPm2?["controlPipeName"]?.GetValue<string>() : null)));
        }

        return new ActivationPlan(items, null);
    }

    private static bool JsonArrayValuesEqual(JsonArray? left, JsonArray? right)
    {
        var leftValues = left?.Select(static node => node?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
        var rightValues = right?.Select(static node => node?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
        return leftValues.SequenceEqual(rightValues, StringComparer.Ordinal);
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

    private static IReadOnlyDictionary<string, string> BuildArgumentPlaceholders(JsonObject binding)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (property, token) in new[]
        {
            ("install", "ROOT_INSTALL"),
            ("data", "ROOT_DATA"),
            ("logs", "ROOT_LOGS")
        })
        {
            if (binding["roots"]?[property]?.GetValue<string>() is { } value)
                result[token] = value;
        }
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
        IManifestHealthGate healthGate,
        IDictionary<string, ComponentControlTarget> controlTargets,
        IReadOnlySet<string> attemptedNewServices) : IDeploymentActivationRollback
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
                if (item.Kind == "windowsService" && !captures[item.ComponentId].Existed)
                {
                    continue;
                }
                if (item.Kind == "pm2Legacy" && controlTargets[item.ComponentId].PmId is null)
                {
                    continue;
                }
                var stopped = await ExecuteRestoreStepAsync(
                    () => item.ControlAdapter.ExecuteAsync(
                        controlTargets[item.ComponentId],
                        ComponentOperationAction.Stop,
                        cancellationToken));
                success &= stopped.Success;
                steps.Add($"恢复前停止 {item.ComponentId}：{stopped.Detail}");
            }

            foreach (var item in items.Reverse())
            {
                if (item.Kind == "windowsService" && !captures[item.ComponentId].Existed &&
                    !attemptedNewServices.Contains(item.ComponentId))
                {
                    steps.Add($"恢复 {item.ComponentId} 旧入口：本次未尝试创建，无需补偿");
                    continue;
                }
                var restored = await ExecuteRestoreStepAsync(
                    () => item.EntrypointAdapter.RestoreAsync(captures[item.ComponentId], cancellationToken));
                success &= restored.Success;
                steps.Add($"恢复 {item.ComponentId} 旧入口：{restored.Detail}");
                if (item.Kind == "pm2Legacy")
                {
                    controlTargets[item.ComponentId] = ControlTargetForSnapshot(item, captures[item.ComponentId]) with
                    {
                        PmId = restored.PmId
                    };
                    if (captures[item.ComponentId].PmId is not null && restored.PmId is null)
                    {
                        success = false;
                    }
                }
            }

            foreach (var item in items.Where(item => captures[item.ComponentId].WasRunning))
            {
                var started = await ExecuteRestoreStepAsync(
                    () => item.ControlAdapter.ExecuteAsync(
                        controlTargets[item.ComponentId],
                        ComponentOperationAction.Start,
                        cancellationToken));
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

        private static async Task<AdapterExecutionResult> ExecuteRestoreStepAsync(
            Func<Task<AdapterExecutionResult>> operation)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception)
            {
                return new AdapterExecutionResult(
                    false,
                    $"恢复步骤异常 {exception.GetType().Name}：{exception.Message}");
            }
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
                try
                {
                    last = await healthGate.ProbeAsync(projectManifest, binding, componentId, cancellationToken);
                }
                catch (Exception exception)
                {
                    return new HealthGateResult(
                        false,
                        $"恢复健康探针异常 {exception.GetType().Name}：{exception.Message}");
                }
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

public sealed class InteractiveAppDeploymentEntrypointAdapter(
    InteractiveEntrypointStateStore entrypoints,
    IInteractiveSessionClaimProvider claims,
    InteractiveSnapshotReader snapshots) : IDeploymentEntrypointAdapter
{
    public string Kind => "interactiveApp";

    public async Task<DeploymentEntrypointCaptureResult> CaptureAsync(
        DeploymentEntrypointTarget target,
        CancellationToken cancellationToken)
    {
        var matches = (await claims.GetClaimsAsync(cancellationToken)).Where(claim =>
            claim.ProjectId == target.ProjectId && claim.Environment == target.Environment &&
            claim.ComponentId == target.ComponentId && claim.BindingError is null).ToArray();
        if (matches.Length != 1)
            return new(false, Detail: "交互程序当前入口声明不唯一或不完整");
        var claim = matches[0];
        if (claim.ExpectedExecutable is null || claim.ExpectedWorkingDirectory is null)
            return new(false, Detail: "交互程序当前入口路径不完整");

        var managed = await entrypoints.ReadAsync(
            target.ProjectId,
            target.Environment,
            target.ComponentId,
            cancellationToken);
        if (managed.Exists && managed.State is null)
            return new(false, Detail: managed.Error ?? "交互程序当前激活入口状态无效");
        if (!Directory.Exists(claim.ExpectedWorkingDirectory))
            return new(false, Detail: "交互程序当前工作目录不存在");

        var read = await snapshots.ReadAsync(claim, cancellationToken);
        var processMatches = read.Snapshot?.Processes.Where(process =>
            process.ProjectId == target.ProjectId && process.Environment == target.Environment &&
            process.ComponentId == target.ComponentId && process.State == "running" &&
            string.Equals(process.Executable, claim.ExpectedExecutable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(process.WorkingDirectory, claim.ExpectedWorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            process.Arguments.SequenceEqual(claim.ExpectedArguments, StringComparer.Ordinal)).ToArray() ?? [];
        if (processMatches.Length > 1)
            return new(false, Detail: "交互程序当前运行进程不唯一");
        if (!File.Exists(claim.ExpectedExecutable))
        {
            if (managed.Exists || processMatches.Length != 0)
                return new(false, Detail: "交互程序已登记或正在运行，但当前 EXE 不存在");
            return new(
                true,
                new DeploymentEntrypointSnapshot(
                    target.ComponentId,
                    target.Kind,
                    target.NativeName,
                    WindowsCommandLine.Build(claim.ExpectedExecutable, claim.ExpectedArguments),
                    false,
                    target.ProjectId,
                    target.Environment,
                    claim.ExpectedExecutable,
                    claim.ExpectedWorkingDirectory,
                    claim.ExpectedArguments,
                    false),
                "交互程序尚未部署；允许首次 Install 写入受控入口");
        }

        return new(
            true,
            new DeploymentEntrypointSnapshot(
                target.ComponentId,
                target.Kind,
                target.NativeName,
                WindowsCommandLine.Build(claim.ExpectedExecutable, claim.ExpectedArguments),
                processMatches.Length == 1,
                target.ProjectId,
                target.Environment,
                claim.ExpectedExecutable,
                claim.ExpectedWorkingDirectory,
                claim.ExpectedArguments,
                managed.Exists),
            "交互程序当前入口、会话归属和运行状态已读取");
    }

    public async Task<AdapterExecutionResult> ApplyAsync(
        DeploymentEntrypointTarget target,
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.ExecutablePath) ||
            target.WorkingDirectory is null || !Directory.Exists(target.WorkingDirectory))
            return new(false, "交互程序新 EXE 或工作目录不存在");
        await entrypoints.WriteAsync(
            new InteractiveEntrypointState(
                InteractiveSessionProtocol.EntrypointStateVersion,
                target.ProjectId,
                target.Environment,
                target.ComponentId,
                target.ExecutablePath,
                target.WorkingDirectory,
                target.Arguments,
                DateTimeOffset.UtcNow),
            cancellationToken);
        return new(true, "交互程序当前激活入口已原子切换");
    }

    public async Task<AdapterExecutionResult> RestoreAsync(
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.ProjectId is null || snapshot.Environment is null ||
            snapshot.ExecutablePath is null || snapshot.WorkingDirectory is null || snapshot.Arguments is null)
            return new(false, "交互程序旧入口快照不完整");
        if (snapshot.HadManagedState)
        {
            await entrypoints.WriteAsync(
                new InteractiveEntrypointState(
                    InteractiveSessionProtocol.EntrypointStateVersion,
                    snapshot.ProjectId,
                    snapshot.Environment,
                    snapshot.ComponentId,
                    snapshot.ExecutablePath,
                    snapshot.WorkingDirectory,
                    snapshot.Arguments,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        else
        {
            await entrypoints.DeleteAsync(
                snapshot.ProjectId,
                snapshot.Environment,
                snapshot.ComponentId,
                cancellationToken);
        }
        return new(true, "交互程序旧入口状态已恢复");
    }
}

public sealed record WindowsServiceObservedState(
    string ServiceName,
    string DisplayName,
    string BinaryPath,
    bool IsRunning,
    string StartMode,
    string ServiceAccountName,
    IReadOnlyList<string> Dependencies,
    int FailureRestartLimit,
    string? Description = null);

public interface IWindowsServiceManager
{
    IReadOnlyList<WindowsServiceObservedState> FindExact(string serviceName);

    AdapterExecutionResult Create(WindowsServiceInstallSpec specification);

    AdapterExecutionResult ChangeBinaryPath(string serviceName, string binaryPath);

    AdapterExecutionResult DeleteCreated(WindowsServiceInstallSpec specification);
}

public sealed class WindowsServiceDeploymentEntrypointAdapter : IDeploymentEntrypointAdapter
{
    private readonly IWindowsServiceManager _services;

    public WindowsServiceDeploymentEntrypointAdapter()
        : this(new NativeWindowsServiceManager())
    {
    }

    public WindowsServiceDeploymentEntrypointAdapter(IWindowsServiceManager services)
    {
        _services = services;
    }

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

        try
        {
            var matches = _services.FindExact(target.NativeName);
            if (matches.Count == 0)
            {
                var specification = target.WindowsService;
                if (!target.AllowCreate)
                {
                    return Task.FromResult(new DeploymentEntrypointCaptureResult(
                        false,
                        Detail: "SCM 精确名称匹配数量为 0；Update/Rollback 不允许补建服务"));
                }
                if (specification is null || string.IsNullOrWhiteSpace(specification.ServiceAccountName))
                {
                    return Task.FromResult(new DeploymentEntrypointCaptureResult(
                        false,
                        Detail: $"首次 Install 的 serviceAccountRef {specification?.ServiceAccountRef ?? "<missing>"} 不是受支持的内置账户引用"));
                }
                if (!ValidInstallSpecification(specification, target))
                {
                    return Task.FromResult(new DeploymentEntrypointCaptureResult(
                        false,
                        Detail: "首次 Install 的 Windows Service 身份、启动模式或依赖声明无效"));
                }
                specification = specification with
                {
                    OwnershipMarker = $"companyops-created/v1/{Guid.NewGuid():N}"
                };
                return Task.FromResult(new DeploymentEntrypointCaptureResult(
                    true,
                    new DeploymentEntrypointSnapshot(
                        target.ComponentId,
                        target.Kind,
                        target.NativeName,
                        target.BinaryPath,
                        false,
                        HostingAdapter: "scmCreate",
                        Existed: false,
                        WindowsService: specification),
                    "SCM 精确名称不存在，已形成仅允许本次 Install 创建的事务快照"));
            }
            if (matches.Count != 1)
            {
                return Task.FromResult(new DeploymentEntrypointCaptureResult(
                    false,
                    Detail: $"SCM 精确名称匹配数量为 {matches.Count}"));
            }

            var service = matches[0];
            if (target.WindowsService is { ServiceAccountName.Length: > 0 } expectedService &&
                !EquivalentExistingPolicy(service, expectedService))
            {
                return Task.FromResult(new DeploymentEntrypointCaptureResult(
                    false,
                    Detail: "SCM 现有服务的显示名、账户、启动模式、依赖或恢复策略与受管声明不一致"));
            }
            var binaryPath = service.BinaryPath;
            var hostExecutable = WindowsCommandLine.ExtractExecutable(binaryPath);
            var isNssm = string.Equals(
                Path.GetFileName(hostExecutable),
                "nssm.exe",
                StringComparison.OrdinalIgnoreCase);
            var nssm = isNssm ? NssmServiceConfiguration.Query(target.NativeName) : null;
            if (isNssm && nssm is null)
                return Task.FromResult(new DeploymentEntrypointCaptureResult(
                    false,
                    Detail: "SCM 使用 NSSM，但 Parameters 入口配置不存在"));
            if (nssm is not null &&
                (string.IsNullOrWhiteSpace(nssm.Application) || string.IsNullOrWhiteSpace(nssm.AppDirectory)))
                return Task.FromResult(new DeploymentEntrypointCaptureResult(
                    false,
                    Detail: "NSSM Application 或 AppDirectory 为空"));
            return Task.FromResult(new DeploymentEntrypointCaptureResult(
                true,
                new DeploymentEntrypointSnapshot(
                    target.ComponentId,
                    target.Kind,
                    target.NativeName,
                    binaryPath,
                    service.IsRunning,
                    HostingAdapter: nssm is null ? "scmImagePath" : "nssmApplication",
                    HostApplication: nssm?.Application,
                    HostWorkingDirectory: nssm?.AppDirectory,
                    HostArguments: nssm?.AppParameters),
                nssm is null ? "SCM 当前入口读取成功" : "SCM/NSSM 当前入口读取成功"));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
            UnauthorizedAccessException or System.Security.SecurityException)
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

        if (!snapshot.Existed)
        {
            if (snapshot.HostingAdapter != "scmCreate" || snapshot.WindowsService is null ||
                target.WindowsService is null ||
                !ValidOwnershipMarker(snapshot.WindowsService.OwnershipMarker) ||
                !EquivalentInstallSpecification(snapshot.WindowsService, target.WindowsService))
            {
                return Task.FromResult(new AdapterExecutionResult(false, "首次 Install 服务事务快照不完整或目标已变化"));
            }
            return Task.FromResult(_services.Create(snapshot.WindowsService));
        }

        return snapshot.HostingAdapter == "nssmApplication"
            ? ChangeNssmAsync(
                target.NativeName,
                target.ExecutablePath,
                target.WorkingDirectory ?? Path.GetDirectoryName(target.ExecutablePath)!,
                WindowsCommandLine.BuildArguments(target.Arguments),
                "NSSM 新应用入口已写入")
            : Task.FromResult(_services.ChangeBinaryPath(target.NativeName, target.BinaryPath));
    }

    public Task<AdapterExecutionResult> RestoreAsync(
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.Existed)
        {
            return Task.FromResult(snapshot.WindowsService is null
                ? new AdapterExecutionResult(false, "首次 Install 服务事务快照不完整")
                : _services.DeleteCreated(snapshot.WindowsService));
        }
        if (snapshot.HostingAdapter == "nssmApplication")
        {
            if (snapshot.HostApplication is null || snapshot.HostWorkingDirectory is null)
                return Task.FromResult(new AdapterExecutionResult(false, "NSSM 旧入口快照不完整"));
            return ChangeNssmAsync(
                snapshot.NativeName,
                snapshot.HostApplication,
                snapshot.HostWorkingDirectory,
                snapshot.HostArguments ?? string.Empty,
                "NSSM 旧应用入口已恢复");
        }
        return Task.FromResult(_services.ChangeBinaryPath(snapshot.NativeName, snapshot.BinaryPath));
    }

    private static Task<AdapterExecutionResult> ChangeNssmAsync(
        string serviceName,
        string application,
        string workingDirectory,
        string arguments,
        string detail)
    {
        try
        {
            NssmServiceConfiguration.Change(serviceName, application, workingDirectory, arguments);
            var actual = NssmServiceConfiguration.Query(serviceName);
            return Task.FromResult(actual is not null &&
                string.Equals(actual.Application, application, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(actual.AppDirectory),
                    Path.TrimEndingDirectorySeparator(workingDirectory),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(actual.AppParameters ?? string.Empty, arguments, StringComparison.Ordinal)
                ? new AdapterExecutionResult(true, detail)
                : new AdapterExecutionResult(false, "NSSM 写入后回读入口不一致"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Task.FromResult(new AdapterExecutionResult(false, exception.Message));
        }
    }

    private static bool SafeNativeName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.IndexOfAny(['\\', '/']) < 0 &&
        value.All(static character => !char.IsControl(character));

    private static bool ValidInstallSpecification(
        WindowsServiceInstallSpec specification,
        DeploymentEntrypointTarget target) =>
        string.Equals(specification.ServiceName, target.NativeName, StringComparison.Ordinal) &&
        string.Equals(specification.BinaryPath, target.BinaryPath, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(specification.DisplayName) &&
        specification.DisplayName.Length <= 256 &&
        specification.StartMode is "automatic" or "delayed" or "manual" &&
        specification.FailureRestartLimit is >= 0 and <= 10 &&
        specification.Dependencies.All(dependency =>
            SafeNativeName(dependency) &&
            !string.Equals(dependency, specification.ServiceName, StringComparison.OrdinalIgnoreCase)) &&
        specification.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() == specification.Dependencies.Count;

    private static bool EquivalentInstallSpecification(
        WindowsServiceInstallSpec left,
        WindowsServiceInstallSpec right) =>
        string.Equals(left.ServiceName, right.ServiceName, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        string.Equals(left.BinaryPath, right.BinaryPath, StringComparison.Ordinal) &&
        string.Equals(left.StartMode, right.StartMode, StringComparison.Ordinal) &&
        string.Equals(left.ServiceAccountRef, right.ServiceAccountRef, StringComparison.Ordinal) &&
        string.Equals(left.ServiceAccountName, right.ServiceAccountName, StringComparison.OrdinalIgnoreCase) &&
        left.Dependencies.SequenceEqual(right.Dependencies, StringComparer.OrdinalIgnoreCase) &&
        left.FailureRestartLimit == right.FailureRestartLimit;

    private static bool EquivalentExistingPolicy(
        WindowsServiceObservedState actual,
        WindowsServiceInstallSpec expected) =>
        string.Equals(actual.ServiceName, expected.ServiceName, StringComparison.Ordinal) &&
        string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal) &&
        string.Equals(actual.StartMode, expected.StartMode, StringComparison.Ordinal) &&
        NormalizeAccount(actual.ServiceAccountName) == NormalizeAccount(expected.ServiceAccountName) &&
        actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.OrdinalIgnoreCase) &&
        actual.FailureRestartLimit == expected.FailureRestartLimit;

    private static string NormalizeAccount(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".\\", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    private static bool ValidOwnershipMarker(string? marker) =>
        marker is { Length: 54 } &&
        marker.StartsWith("companyops-created/v1/", StringComparison.Ordinal) &&
        Guid.TryParseExact(marker[22..], "N", out _);
}

internal static class WindowsCommandLine
{
    public static string Build(string executablePath, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Quote(executablePath) }.Concat(arguments.Select(Quote)));

    public static string BuildArguments(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    public static string ExtractExecutable(string commandLine)
    {
        var value = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : string.Empty;
        }
        var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? value[..(executableEnd + 4)] : value.Split(' ', 2)[0];
    }

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

internal sealed record NssmServiceEntrypoint(
    string Application,
    string AppDirectory,
    string? AppParameters);

internal static class NssmServiceConfiguration
{
    public static NssmServiceEntrypoint? Query(string serviceName)
    {
        using var parameters = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}\Parameters",
            writable: false);
        if (parameters is null) return null;
        return new NssmServiceEntrypoint(
            parameters.GetValue("Application") as string ?? string.Empty,
            parameters.GetValue("AppDirectory") as string ?? string.Empty,
            parameters.GetValue("AppParameters") as string);
    }

    public static void Change(
        string serviceName,
        string application,
        string workingDirectory,
        string arguments)
    {
        using var parameters = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}\Parameters",
            writable: true) ?? throw new IOException($"服务 {serviceName} 缺少 NSSM Parameters");
        parameters.SetValue("Application", application, RegistryValueKind.String);
        parameters.SetValue("AppDirectory", workingDirectory, RegistryValueKind.String);
        parameters.SetValue("AppParameters", arguments, RegistryValueKind.String);
        parameters.Flush();
    }
}

internal static class WindowsServiceAccount
{
    public static string? ResolveBuiltIn(string reference) => reference switch
    {
        "local-system" => "LocalSystem",
        "local-service" => @"NT AUTHORITY\LocalService",
        "network-service" => @"NT AUTHORITY\NetworkService",
        _ => null
    };
}

internal sealed class NativeWindowsServiceManager : IWindowsServiceManager
{
    public IReadOnlyList<WindowsServiceObservedState> FindExact(string serviceName)
    {
        var controllers = System.ServiceProcess.ServiceController.GetServices();
        try
        {
            var matches = controllers
                .Where(service => string.Equals(service.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var result = new List<WindowsServiceObservedState>(matches.Length);
            foreach (var service in matches)
            {
                service.Refresh();
                if (service.Status is not (System.ServiceProcess.ServiceControllerStatus.Running or
                    System.ServiceProcess.ServiceControllerStatus.Stopped))
                {
                    throw new InvalidOperationException($"SCM 当前状态 {service.Status} 不允许入口迁移");
                }
                var configuration = WindowsServiceConfiguration.Query(service.ServiceName);
                result.Add(new WindowsServiceObservedState(
                    service.ServiceName,
                    configuration.DisplayName,
                    configuration.BinaryPath,
                    service.Status == System.ServiceProcess.ServiceControllerStatus.Running,
                    configuration.StartMode,
                    configuration.ServiceAccountName,
                    configuration.Dependencies,
                    configuration.FailureRestartLimit,
                    configuration.Description));
            }
            return result;
        }
        finally
        {
            foreach (var controller in controllers)
            {
                controller.Dispose();
            }
        }
    }

    public AdapterExecutionResult Create(WindowsServiceInstallSpec specification)
    {
        try
        {
            if (!ValidOwnershipMarker(specification.OwnershipMarker))
            {
                return new AdapterExecutionResult(false, "SCM 首次创建缺少有效的随机所有权标记");
            }
            WindowsServiceConfiguration.Create(specification);
            var matches = FindExact(specification.ServiceName);
            if (matches.Count == 1 && Matches(matches[0], specification))
            {
                return new AdapterExecutionResult(true, "SCM 服务已按精确身份和所有权标记创建并回读验证");
            }
            var cleanup = WindowsServiceConfiguration.DeleteIfOwned(specification, TimeSpan.FromSeconds(30));
            return new AdapterExecutionResult(
                false,
                cleanup.Success
                    ? "SCM 创建后配置回读不一致；已删除本次创建服务"
                    : $"SCM 创建后配置回读不一致，且补偿删除失败：{cleanup.Detail}");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return new AdapterExecutionResult(false, exception.Message);
        }
    }

    public AdapterExecutionResult ChangeBinaryPath(string serviceName, string binaryPath)
    {
        try
        {
            WindowsServiceConfiguration.ChangeBinaryPath(serviceName, binaryPath);
            var matches = FindExact(serviceName);
            return matches.Count == 1 && string.Equals(matches[0].BinaryPath, binaryPath, StringComparison.Ordinal)
                ? new AdapterExecutionResult(true, "SCM 入口已写入并回读验证")
                : new AdapterExecutionResult(false, "SCM 写入后回读入口不一致");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return new AdapterExecutionResult(false, exception.Message);
        }
    }

    public AdapterExecutionResult DeleteCreated(WindowsServiceInstallSpec specification)
    {
        try
        {
            var matches = FindExact(specification.ServiceName);
            if (matches.Count == 0)
            {
                return new AdapterExecutionResult(true, "本次 Install 未留下 Windows Service");
            }
            if (matches.Count != 1 || !Matches(matches[0], specification))
            {
                return new AdapterExecutionResult(false, "待删除服务不再精确匹配本次创建身份，拒绝删除");
            }
            var deleted = WindowsServiceConfiguration.DeleteIfOwned(specification, TimeSpan.FromSeconds(30));
            if (!deleted.Success)
            {
                return deleted;
            }
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline && FindExact(specification.ServiceName).Count != 0)
            {
                Thread.Sleep(100);
            }
            return FindExact(specification.ServiceName).Count == 0
                ? new AdapterExecutionResult(true, "本次 Install 创建的 Windows Service 已停止并删除")
                : new AdapterExecutionResult(false, "SCM 删除后服务仍存在");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
            UnauthorizedAccessException or InvalidOperationException or TimeoutException or
            System.Security.SecurityException)
        {
            return new AdapterExecutionResult(false, exception.Message);
        }
    }

    private static bool Matches(WindowsServiceObservedState actual, WindowsServiceInstallSpec expected) =>
        string.Equals(actual.ServiceName, expected.ServiceName, StringComparison.Ordinal) &&
        string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal) &&
        string.Equals(actual.BinaryPath, expected.BinaryPath, StringComparison.Ordinal) &&
        string.Equals(actual.StartMode, expected.StartMode, StringComparison.Ordinal) &&
        SameAccount(actual.ServiceAccountName, expected.ServiceAccountName) &&
        actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.OrdinalIgnoreCase) &&
        actual.FailureRestartLimit == expected.FailureRestartLimit &&
        string.Equals(actual.Description, expected.OwnershipMarker, StringComparison.Ordinal);

    private static bool ValidOwnershipMarker(string? marker) =>
        marker is { Length: 54 } && marker.StartsWith("companyops-created/v1/", StringComparison.Ordinal) &&
        Guid.TryParseExact(marker[22..], "N", out _);

    private static bool SameAccount(string left, string right)
    {
        static string Normalize(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".\\", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return Normalize(left) == Normalize(right);
    }
}

internal sealed record WindowsServiceConfigurationSnapshot(
    string DisplayName,
    string BinaryPath,
    string StartMode,
    string ServiceAccountName,
    IReadOnlyList<string> Dependencies,
    int FailureRestartLimit,
    string? Description);

internal static class WindowsServiceConfiguration
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ScActionRestart = 1;
    private const uint ServiceNoChange = 0xffffffff;
    private const int ErrorInsufficientBuffer = 122;

    public static WindowsServiceConfigurationSnapshot Query(string serviceName)
    {
        using var service = Open(serviceName, ServiceQueryConfig);
        return Query(serviceName, service);
    }

    private static WindowsServiceConfigurationSnapshot Query(string serviceName, SafeServiceHandle service)
    {
        var config = QueryBaseConfiguration(serviceName, service);
        var delayed = QueryDelayedAutoStart(serviceName, service);
        var failureRestartLimit = QueryFailureRestartLimit(serviceName, service);
        var description = QueryDescription(serviceName, service);
        var startMode = config.StartType switch
        {
            ServiceAutoStart when delayed => "delayed",
            ServiceAutoStart => "automatic",
            ServiceDemandStart => "manual",
            _ => $"unsupported:{config.StartType}"
        };
        return new WindowsServiceConfigurationSnapshot(
            config.DisplayName,
            config.BinaryPath,
            startMode,
            config.ServiceStartName,
            config.Dependencies,
            failureRestartLimit,
            description);
    }

    public static string QueryBinaryPath(string serviceName)
    {
        using var service = Open(serviceName, ServiceQueryConfig);
        var config = QueryBaseConfiguration(serviceName, service);
        return config.BinaryPath;
    }

    public static void Create(WindowsServiceInstallSpec specification)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "连接 Windows SCM 失败");
        }
        var dependencies = specification.Dependencies.Count == 0
            ? null
            : string.Join('\0', specification.Dependencies) + "\0\0";
        var accountName = string.Equals(specification.ServiceAccountName, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? null
            : specification.ServiceAccountName;
        var startType = specification.StartMode == "manual" ? ServiceDemandStart : ServiceAutoStart;
        using var service = CreateService(
            manager,
            specification.ServiceName,
            specification.DisplayName,
            ServiceChangeConfig | ServiceQueryConfig | ServiceStop | Delete,
            ServiceWin32OwnProcess,
            startType,
            ServiceErrorNormal,
            specification.BinaryPath,
            null,
            IntPtr.Zero,
            dependencies,
            accountName,
            null);
        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"创建服务 {specification.ServiceName} 失败");
        }
        try
        {
            SetDescription(service, specification.OwnershipMarker!);
            SetDelayedAutoStart(service, specification.StartMode == "delayed");
            SetFailureActions(service, specification.FailureRestartLimit);
            var actual = Query(specification.ServiceName, service);
            if (!MatchesOwned(actual, specification))
            {
                throw new IOException($"创建服务 {specification.ServiceName} 后配置回读不一致");
            }
        }
        catch (Exception exception)
        {
            if (!DeleteService(service))
            {
                throw new InvalidOperationException(
                    $"创建服务 {specification.ServiceName} 的附加配置失败，且使用创建句柄补偿删除失败 " +
                    $"({new Win32Exception(Marshal.GetLastWin32Error()).Message})",
                    exception);
            }
            throw;
        }
    }

    public static AdapterExecutionResult DeleteIfOwned(
        WindowsServiceInstallSpec specification,
        TimeSpan timeout)
    {
        using var service = Open(
            specification.ServiceName,
            Delete | ServiceQueryConfig | ServiceStop);
        var before = Query(specification.ServiceName, service);
        if (!MatchesOwned(before, specification))
        {
            return new AdapterExecutionResult(false, "待删除服务的所有权标记或核心身份已变化，拒绝删除");
        }
        using (var controller = new System.ServiceProcess.ServiceController(specification.ServiceName))
        {
            controller.Refresh();
            if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                if (!controller.CanStop)
                {
                    return new AdapterExecutionResult(false, $"服务 {specification.ServiceName} 无法停止，拒绝删除");
                }
                controller.Stop();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, timeout);
            }
        }
        var immediatelyBeforeDelete = Query(specification.ServiceName, service);
        if (!MatchesOwned(immediatelyBeforeDelete, specification))
        {
            return new AdapterExecutionResult(false, "服务停止期间所有权标记或核心身份已变化，拒绝删除");
        }
        if (!DeleteService(service))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"删除服务 {specification.ServiceName} 失败");
        }
        return new AdapterExecutionResult(true, "已用同一 SCM 句柄核验所有权并删除服务");
    }

    private static bool MatchesOwned(
        WindowsServiceConfigurationSnapshot actual,
        WindowsServiceInstallSpec expected) =>
        string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal) &&
        string.Equals(actual.BinaryPath, expected.BinaryPath, StringComparison.Ordinal) &&
        string.Equals(actual.StartMode, expected.StartMode, StringComparison.Ordinal) &&
        string.Equals(actual.ServiceAccountName.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace(".\\", string.Empty, StringComparison.Ordinal),
            expected.ServiceAccountName.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace(".\\", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase) &&
        actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.OrdinalIgnoreCase) &&
        actual.FailureRestartLimit == expected.FailureRestartLimit &&
        string.Equals(actual.Description, expected.OwnershipMarker, StringComparison.Ordinal);

    private static BaseServiceConfiguration QueryBaseConfiguration(string serviceName, SafeServiceHandle service)
    {
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
            return new BaseServiceConfiguration(
                config.StartType,
                Marshal.PtrToStringUni(config.BinaryPathName)
                    ?? throw new Win32Exception($"服务 {serviceName} 的入口为空"),
                Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
                Marshal.PtrToStringUni(config.DisplayName) ?? string.Empty,
                ReadMultiString(config.Dependencies));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool QueryDelayedAutoStart(string serviceName, SafeServiceHandle service)
    {
        var size = (uint)Marshal.SizeOf<ServiceDelayedAutoStartInfo>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, buffer, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取服务 {serviceName} 延迟启动配置失败");
            return Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(buffer).DelayedAutoStart;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int QueryFailureRestartLimit(string serviceName, SafeServiceHandle service)
    {
        _ = QueryServiceConfig2(service, ServiceConfigFailureActions, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
            throw new Win32Exception(error, $"读取服务 {serviceName} 恢复策略缓冲区失败");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig2(service, ServiceConfigFailureActions, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取服务 {serviceName} 恢复策略失败");
            var actions = Marshal.PtrToStructure<ServiceFailureActions>(buffer);
            var actionSize = Marshal.SizeOf<ServiceFailureAction>();
            if (actions.ActionCount > 64)
                throw new IOException($"服务 {serviceName} 恢复动作数量超出安全上限");
            if (actions.ActionCount > 0)
            {
                if (actions.Actions == IntPtr.Zero)
                    throw new IOException($"服务 {serviceName} 恢复动作指针为空");
                var bufferStart = buffer.ToInt64();
                var bufferEnd = checked(bufferStart + required);
                var actionsStart = actions.Actions.ToInt64();
                var actionsEnd = checked(actionsStart + checked((long)actions.ActionCount * actionSize));
                if (actionsStart < bufferStart || actionsEnd > bufferEnd || actionsEnd < actionsStart)
                    throw new IOException($"服务 {serviceName} 恢复动作超出 SCM 返回缓冲区");
            }
            var restartCount = 0;
            for (var index = 0; index < actions.ActionCount; index++)
            {
                var action = Marshal.PtrToStructure<ServiceFailureAction>(
                    IntPtr.Add(actions.Actions, checked(index * actionSize)));
                if (action.Type == ScActionRestart) restartCount++;
            }
            return restartCount;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryDescription(string serviceName, SafeServiceHandle service)
    {
        _ = QueryServiceConfig2(service, ServiceConfigDescription, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
            throw new Win32Exception(error, $"读取服务 {serviceName} 描述缓冲区失败");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig2(service, ServiceConfigDescription, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取服务 {serviceName} 描述失败");
            var description = Marshal.PtrToStructure<ServiceDescription>(buffer);
            return description.Value == IntPtr.Zero ? null : Marshal.PtrToStringUni(description.Value);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetDescription(SafeServiceHandle service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        try
        {
            WithNativeStructure(new ServiceDescription { Value = text }, pointer =>
            {
                if (!ChangeServiceConfig2(service, ServiceConfigDescription, pointer))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置服务所有权描述失败");
            });
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void SetDelayedAutoStart(SafeServiceHandle service, bool delayed)
    {
        var value = new ServiceDelayedAutoStartInfo { DelayedAutoStart = delayed };
        WithNativeStructure(value, pointer =>
        {
            if (!ChangeServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, pointer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "设置服务延迟启动配置失败");
        });
    }

    private static void SetFailureActions(SafeServiceHandle service, int restartLimit)
    {
        IntPtr actionsPointer = IntPtr.Zero;
        try
        {
            if (restartLimit > 0)
            {
                var actionSize = Marshal.SizeOf<ServiceFailureAction>();
                actionsPointer = Marshal.AllocHGlobal(checked(actionSize * restartLimit));
                for (var index = 0; index < restartLimit; index++)
                {
                    Marshal.StructureToPtr(
                        new ServiceFailureAction { Type = ScActionRestart, DelayMilliseconds = 60_000 },
                        IntPtr.Add(actionsPointer, index * actionSize),
                        false);
                }
            }
            var value = new ServiceFailureActions
            {
                ResetPeriodSeconds = 86_400,
                ActionCount = checked((uint)restartLimit),
                Actions = actionsPointer
            };
            WithNativeStructure(value, pointer =>
            {
                if (!ChangeServiceConfig2(service, ServiceConfigFailureActions, pointer))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置服务恢复策略失败");
            });
        }
        finally
        {
            if (actionsPointer != IntPtr.Zero) Marshal.FreeHGlobal(actionsPointer);
        }
    }

    private static void WithNativeStructure<T>(T value, Action<IntPtr> operation) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            operation(pointer);
        }
        finally
        {
            Marshal.DestroyStructure<T>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static IReadOnlyList<string> ReadMultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var result = new List<string>();
        var offset = 0;
        while (true)
        {
            var value = Marshal.PtrToStringUni(IntPtr.Add(pointer, offset)) ?? string.Empty;
            if (value.Length == 0) return result;
            result.Add(value);
            offset = checked(offset + (value.Length + 1) * sizeof(char));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DelayedAutoStart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriodSeconds;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    private sealed record BaseServiceConfiguration(
        uint StartType,
        string BinaryPath,
        string ServiceStartName,
        string DisplayName,
        IReadOnlyList<string> Dependencies);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureAction
    {
        public int Type;
        public uint DelayMilliseconds;
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

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle CreateService(
        SafeServiceHandle serviceControlManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

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

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeServiceHandle service,
        uint infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeServiceHandle service,
        uint infoLevel,
        IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
