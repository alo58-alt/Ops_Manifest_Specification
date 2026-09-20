using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Deployment;

public interface IPm2OwnerMutationBridge
{
    Task<Pm2BridgeMutationResponse> ExecuteAsync(
        string pipeName,
        Pm2BridgeMutationRequest request,
        CancellationToken cancellationToken);
}

public sealed class NamedPipePm2OwnerMutationBridge(JsonSerializerOptions jsonOptions) : IPm2OwnerMutationBridge
{
    public async Task<Pm2BridgeMutationResponse> ExecuteAsync(
        string pipeName,
        Pm2BridgeMutationRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions), timeout.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            return line is null
                ? Failure(request, "PM2 owner bridge 响应为空")
                : JsonSerializer.Deserialize<Pm2BridgeMutationResponse>(line, jsonOptions)
                  ?? Failure(request, "PM2 owner bridge 响应无效");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return Failure(request, $"PM2 owner bridge 不可用：{exception.Message}");
        }
    }

    private static Pm2BridgeMutationResponse Failure(Pm2BridgeMutationRequest request, string detail) =>
        new(Pm2BridgeProtocol.MutationVersion, request.RequestId, false, null, "bridge_unavailable", detail);
}

public sealed class Pm2LegacyDeploymentEntrypointAdapter(
    Pm2SnapshotReader snapshots,
    IPm2OwnerMutationBridge mutations) : IDeploymentEntrypointAdapter
{
    private readonly ConcurrentDictionary<string, Pm2BridgeProcessIdentity> _registered = new(StringComparer.Ordinal);

    public string Kind => "pm2Legacy";

    public async Task<DeploymentEntrypointCaptureResult> CaptureAsync(
        DeploymentEntrypointTarget target,
        CancellationToken cancellationToken)
    {
        if (target.InstallRoot is null || target.SnapshotFileName is null || target.OwnerSid is null ||
            target.ControlPipeName is null || target.WorkingDirectory is null)
        {
            return new(false, Detail: "PM2 EnvironmentBinding 或候选入口不完整");
        }

        var read = await snapshots.ReadAsync(
            new LegacyPm2Claim(
                target.ProjectId, target.Environment, string.Empty, target.ComponentId, target.ComponentId,
                target.NativeName, target.WorkingDirectory, target.ExecutablePath, target.OwnerSid,
                target.SnapshotFileName, target.ControlPipeName, target.SnapshotMaxAgeSeconds, null),
            cancellationToken);
        if (read.State != Pm2OwnershipState.Matched || read.Snapshot is null)
        {
            return new(false, Detail: read.Detail);
        }

        var sameName = read.Snapshot.Processes.Where(process =>
            string.Equals(process.Name, target.NativeName, StringComparison.Ordinal)).ToArray();
        var old = await ReadCurrentIdentityAsync(target, cancellationToken);
        if (old.Error is not null)
        {
            return new(false, Detail: old.Error);
        }

        if (old.Identity is null)
        {
            if (sameName.Length == 0)
            {
                return new(true, new DeploymentEntrypointSnapshot(
                    target.ComponentId, target.Kind, target.NativeName, string.Empty, false,
                    target.ProjectId, target.Environment, ControlPipeName: target.ControlPipeName),
                    "首次安装已由新鲜快照证明同名实例为零");
            }
            if (sameName.Length != 1 || target.LegacyCwd is null || target.LegacyScript is null)
            {
                return new(false, Detail: $"首次安装发现 {sameName.Length} 个同名 PM2 实例且旧声明身份不完整");
            }
            var existing = sameName[0];
            var baseline = new Pm2BridgeProcessIdentity(existing.PmId, target.NativeName,
                target.LegacyCwd, target.LegacyScript, target.LegacyArguments ?? []);
            if (!Matches(existing, baseline))
            {
                return new(false, Detail: "首次迁移的 PM2 入口未完整匹配 ProjectManifest name/cwd/script/arguments");
            }
            return new(true, new DeploymentEntrypointSnapshot(
                target.ComponentId, target.Kind, target.NativeName,
                WindowsCommandLine.Build(baseline.Script, baseline.Arguments),
                string.Equals(existing.Status, "online", StringComparison.OrdinalIgnoreCase),
                target.ProjectId, target.Environment, baseline.Script, baseline.Cwd, baseline.Arguments,
                PmId: existing.PmId, ControlPipeName: target.ControlPipeName),
                $"首次迁移已唯一匹配旧 PM2 pm_id={existing.PmId}");
        }

        if (sameName.Length != 1)
        {
            return new(false, Detail: $"PM2 精确名称匹配数量为 {sameName.Length}");
        }

        var process = sameName[0];
        if (!Matches(process, old.Identity))
        {
            return new(false, Detail: "PM2 旧入口 name/cwd/script/arguments 与当前受控 release 不一致");
        }

        return new(true, new DeploymentEntrypointSnapshot(
            target.ComponentId, target.Kind, target.NativeName,
            WindowsCommandLine.Build(old.Identity.Script, old.Identity.Arguments),
            string.Equals(process.Status, "online", StringComparison.OrdinalIgnoreCase),
            target.ProjectId, target.Environment, old.Identity.Script, old.Identity.Cwd,
            old.Identity.Arguments, PmId: process.PmId,
            ControlPipeName: target.ControlPipeName),
            $"新鲜快照已唯一匹配旧 PM2 pm_id={process.PmId}");
    }

    public async Task<AdapterExecutionResult> ApplyAsync(
        DeploymentEntrypointTarget target,
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (target.ControlPipeName is null || target.WorkingDirectory is null)
        {
            return new(false, "PM2 候选入口不完整");
        }

        if (snapshot.PmId is int oldPmId)
        {
            var deletion = await MutateAsync(target.ControlPipeName, Pm2BridgeMutationAction.Delete,
                new Pm2BridgeProcessIdentity(oldPmId, snapshot.NativeName, snapshot.WorkingDirectory!,
                    snapshot.ExecutablePath!, snapshot.Arguments ?? []), null, cancellationToken);
            if (!deletion.Success)
            {
                return new(false, $"精确删除旧 PM2 入口失败：{deletion.Detail}");
            }
        }

        var registration = await MutateAsync(target.ControlPipeName, Pm2BridgeMutationAction.Register, null,
            new Pm2BridgeRegistration(target.NativeName, target.WorkingDirectory, target.ExecutablePath, target.Arguments),
            cancellationToken);
        if (!registration.Success || registration.Process is null)
        {
            return new(false, $"登记新 PM2 入口失败：{registration.Detail}");
        }

        _registered[Key(target.ProjectId, target.Environment, target.ComponentId)] = registration.Process;
        return new(true, $"已登记新 PM2 入口 pm_id={registration.Process.PmId}", registration.Process.PmId);
    }

    public async Task<AdapterExecutionResult> RestoreAsync(
        DeploymentEntrypointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.ProjectId is null || snapshot.Environment is null || snapshot.ControlPipeName is null)
        {
            return new(false, "PM2 恢复快照不完整");
        }

        if (_registered.TryRemove(Key(snapshot.ProjectId, snapshot.Environment, snapshot.ComponentId), out var registered))
        {
            var deletion = await MutateAsync(snapshot.ControlPipeName, Pm2BridgeMutationAction.Delete,
                registered, null, cancellationToken);
            if (!deletion.Success)
            {
                return new(false, $"删除本次新登记 PM2 入口失败：{deletion.Detail}");
            }
        }

        if (snapshot.PmId is null)
        {
            return new(true, "首次安装入口已移除");
        }

        var registration = await MutateAsync(snapshot.ControlPipeName, Pm2BridgeMutationAction.Register, null,
            new Pm2BridgeRegistration(snapshot.NativeName, snapshot.WorkingDirectory!, snapshot.ExecutablePath!,
                snapshot.Arguments ?? []), cancellationToken);
        return registration.Success && registration.Process is not null
            ? new(true, $"旧 PM2 入口已恢复为 pm_id={registration.Process.PmId}", registration.Process.PmId)
            : new(false, $"重新登记旧 PM2 入口失败：{registration.Detail}");
    }

    private async Task<Pm2BridgeMutationResponse> MutateAsync(
        string pipeName,
        Pm2BridgeMutationAction action,
        Pm2BridgeProcessIdentity? expected,
        Pm2BridgeRegistration? registration,
        CancellationToken cancellationToken) =>
        await mutations.ExecuteAsync(pipeName,
            new Pm2BridgeMutationRequest(Pm2BridgeProtocol.MutationVersion, Guid.CreateVersion7().ToString(),
                action, expected, registration), cancellationToken);

    private static async Task<(Pm2BridgeProcessIdentity? Identity, string? Error)> ReadCurrentIdentityAsync(
        DeploymentEntrypointTarget target,
        CancellationToken cancellationToken)
    {
        var pointerPath = Path.Combine(target.InstallRoot!, "current.release.json");
        if (!File.Exists(pointerPath))
        {
            return (null, null);
        }

        try
        {
            var pointer = JsonNode.Parse(await File.ReadAllTextAsync(pointerPath, cancellationToken))?.AsObject();
            var currentPath = pointer?["currentPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(currentPath) || !IsUnderRoot(currentPath, Path.Combine(target.InstallRoot!, "releases")))
            {
                return (null, "current.release.json 的当前 release 路径不可信");
            }

            var manifestPath = Path.Combine(currentPath, ".companyops", "release-manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))?.AsObject();
            var payloads = manifest?["componentPayloads"]?.AsArray().OfType<JsonObject>()
                .Where(item => item["componentId"]?.GetValue<string>() == target.ComponentId).ToArray() ?? [];
            if (payloads.Length != 1 || payloads[0]["pm2"] is not JsonObject pm2)
            {
                return (null, "当前受控 release 缺少唯一 PM2 发布身份");
            }

            var artifactId = payloads[0]["artifactId"]!.GetValue<string>();
            var artifactRoot = Path.Combine(currentPath, artifactId);
            var cwd = ResolveUnderRoot(artifactRoot, pm2["cwd"]?.GetValue<string>());
            var script = ResolveUnderRoot(artifactRoot, pm2["script"]?.GetValue<string>());
            var name = pm2["name"]?.GetValue<string>();
            var arguments = new List<string>();
            foreach (var node in pm2["arguments"]?.AsArray() ?? [])
            {
                var argument = ResolveArgument(node!.GetValue<string>(), target.ArgumentValues ?? new Dictionary<string, string>());
                if (argument is null)
                {
                    return (null, "当前受控 release 的 PM2 参数包含未知占位符");
                }
                arguments.Add(argument);
            }
            if (cwd is null || script is null || name != target.NativeName)
            {
                return (null, "当前受控 release 的 PM2 路径或名称无效");
            }

            return (new Pm2BridgeProcessIdentity(-1, name, cwd, script, arguments), null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return (null, $"读取当前 PM2 release 身份失败：{exception.Message}");
        }
    }

    private static bool Matches(Pm2ProcessSnapshot process, Pm2BridgeProcessIdentity identity) =>
        SamePath(process.Cwd, identity.Cwd) && SamePath(process.Script, identity.Script) &&
        (process.Arguments ?? []).SequenceEqual(identity.Arguments, StringComparer.Ordinal);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static string? ResolveUnderRoot(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var fullRoot = Path.GetFullPath(root);
        var value = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return IsUnderRoot(value, fullRoot) ? value : null;
    }

    private static string? ResolveArgument(string value, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            value = value.Replace("${" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        }
        return value.Contains("${", StringComparison.Ordinal) ? null : value;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string projectId, string environment, string componentId) =>
        $"{projectId}\n{environment}\n{componentId}";
}
