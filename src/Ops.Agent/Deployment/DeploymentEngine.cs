using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CompanyOps.Agent.Deployment;

public sealed record DeploymentActivationResult(bool Success, string? Detail = null);

public interface IDeploymentActivator
{
    Task<DeploymentActivationResult> ActivateAsync(
        string projectId,
        string environment,
        string releasePath,
        CancellationToken cancellationToken);
}

public sealed class ReleasePointerDeploymentActivator : IDeploymentActivator
{
    public Task<DeploymentActivationResult> ActivateAsync(
        string projectId,
        string environment,
        string releasePath,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Directory.Exists(releasePath)
                ? new DeploymentActivationResult(true, "不可变 release 已就绪；原生资源仍由白名单控制适配器管理")
                : new DeploymentActivationResult(false, "release 目录不存在"));
}

public sealed class DeploymentEngine(
    AgentSnapshotCache snapshotCache,
    ArtifactPackageValidator packageValidator,
    SafeZipExtractor zipExtractor,
    IPortRegistryStore portRegistry,
    IDeploymentActivator activator,
    OperationGate operationGate,
    IOpsStateStore stateStore,
    OpsPathResolver pathResolver,
    IOptions<OpsOptions> options,
    JsonSerializerOptions jsonOptions)
{
    private readonly OpsOptions _options = options.Value;
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();
    private readonly ConcurrentDictionary<string, IdempotentDeployment> _deployments =
        new(StringComparer.Ordinal);

    public Task<DeploymentResult> ExecuteAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
        {
            return Task.FromResult(Reject(request, "invalid_idempotency_key", "IdempotencyKey 不能为空且不能超过 200 字符"));
        }

        var fingerprint = JsonSerializer.Serialize(request with { IdempotencyKey = string.Empty }, jsonOptions);
        var candidate = new IdempotentDeployment(
            fingerprint,
            new Lazy<Task<DeploymentResult>>(
                () => ExecuteCoreAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _deployments.GetOrAdd(request.IdempotencyKey, candidate);
        return string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? operation.Execution.Value
            : Task.FromResult(Reject(
                request,
                "idempotency_conflict",
                "同一 IdempotencyKey 已用于不同部署请求"));
    }

    private async Task<DeploymentResult> ExecuteCoreAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Action == DeploymentAction.Rollback)
        {
            return await RollbackAsync(request, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.ReleaseManifestPath) ||
            string.IsNullOrWhiteSpace(request.ArtifactDirectory))
        {
            return Reject(request, "release_input_required", "发布需要 ReleaseManifestPath 和 ArtifactDirectory");
        }

        var validation = await packageValidator.ValidateAsync(
            request.ReleaseManifestPath,
            request.ArtifactDirectory,
            cancellationToken);
        if (!validation.Success || !string.Equals(validation.ProjectId, request.ProjectId, StringComparison.Ordinal))
        {
            return Reject(request, "artifact_validation_failed", string.Join("；", validation.Errors));
        }

        var context = await LoadContextAsync(request, validation.Version, cancellationToken);
        if (context.Error is not null)
        {
            return Reject(request, context.Error.Value.Code, context.Error.Value.Detail);
        }

        var steps = new List<string>
        {
            "ReleaseManifest 与制品大小/SHA-256 校验通过",
            "EnvironmentBinding、InstalledState generation 与项目归属校验通过",
            $"计划目标：{context.ReleasePath}"
        };
        if (request.Action == DeploymentAction.Plan)
        {
            return Success(request, context.CurrentVersion, validation.Version, steps);
        }

        if (!_options.EnableMutations)
        {
            return Reject(request, "mutations_disabled", "Agent 未启用部署变更", steps);
        }

        using var lease = operationGate.TryAcquire(
            new[] { $"project:{request.ProjectId}:{request.Environment}" }
                .Concat(context.Ports.Select(static port => $"port:{port.Protocol}:{port.Address}:{port.Port}")));
        if (lease is null)
        {
            return Reject(request, "resource_busy", "项目或端口资源正在执行其他操作", steps);
        }

        var reservation = await portRegistry.ReserveAsync(context.Ports, cancellationToken);
        if (!reservation.Success)
        {
            return Reject(request, reservation.ErrorCode ?? "port_reservation_failed", reservation.Detail ?? "端口预留失败", steps);
        }

        steps.Add("端口批量事务预留成功");
        var stagingPath = context.StagingPath;
        try
        {
            if (Directory.Exists(stagingPath) || Directory.Exists(context.ReleasePath))
            {
                return Reject(request, "release_path_exists", "staging 或目标 release 已存在，拒绝覆盖", steps);
            }

            Directory.CreateDirectory(stagingPath);
            foreach (var artifact in validation.Artifacts)
            {
                await zipExtractor.ExtractAsync(
                    artifact,
                    Path.Combine(stagingPath, artifact.Id),
                    cancellationToken);
            }

            await ValidatePayloadsAsync(request.ReleaseManifestPath, stagingPath, cancellationToken);
            steps.Add("ZIP 安全解包与 componentPayload 路径校验通过");
            Directory.CreateDirectory(Path.GetDirectoryName(context.ReleasePath)!);
            Directory.Move(stagingPath, context.ReleasePath);
            var metadataDirectory = Path.Combine(context.ReleasePath, ".companyops");
            Directory.CreateDirectory(metadataDirectory);
            File.Copy(
                request.ReleaseManifestPath,
                Path.Combine(metadataDirectory, "release-manifest.json"),
                overwrite: false);
            steps.Add("staging 原子移动为不可变 release");

            var activation = await activator.ActivateAsync(
                request.ProjectId,
                request.Environment,
                context.ReleasePath,
                cancellationToken);
            if (!activation.Success)
            {
                await QuarantineFailedReleaseAsync(context, request.OperationId);
                await portRegistry.ReleaseOperationAsync(request.OperationId, cancellationToken);
                return Reject(request, "activation_failed", activation.Detail ?? "激活失败", steps);
            }

            await WriteReleasePointerAsync(context, validation.Version, cancellationToken);
            await WriteInstalledStateAsync(request, context, validation.Version, cancellationToken);
            await portRegistry.CommitOperationAsync(request.OperationId, cancellationToken);
            steps.Add("release pointer、InstalledState 与端口登记已提交");
            var result = Success(request, context.CurrentVersion, validation.Version, steps);
            await AuditAsync(result, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await QuarantineFailedReleaseAsync(context, request.OperationId);
            await portRegistry.ReleaseOperationAsync(request.OperationId, cancellationToken);
            var result = Reject(request, "deployment_failed", exception.Message, steps);
            await AuditAsync(result, cancellationToken);
            return result;
        }
    }

    private async Task<DeploymentResult> RollbackAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableMutations)
        {
            return Reject(request, "mutations_disabled", "Agent 未启用回滚变更");
        }

        var binding = await FindBindingAsync(request.ProjectId, request.Environment, cancellationToken);
        if (binding is null)
        {
            return Reject(request, "binding_not_unique", "无法取得唯一 EnvironmentBinding");
        }

        var project = snapshotCache.Read().Projects?.Projects.SingleOrDefault(item =>
            item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        if (project is null || project.Status == ProjectBindingStatus.Conflict ||
            project.Generation != request.ExpectedGeneration)
        {
            return Reject(request, "generation_or_ownership_conflict", "项目归属冲突或 generation 已变化");
        }

        var installRoot = Path.GetFullPath(binding["roots"]!["install"]!.GetValue<string>());
        var pointerPath = Path.Combine(installRoot, "current.release.json");
        if (!File.Exists(pointerPath))
        {
            return Reject(request, "rollback_unavailable", "缺少 current.release.json");
        }

        var pointer = JsonNode.Parse(await File.ReadAllTextAsync(pointerPath, cancellationToken))!.AsObject();
        var currentVersion = pointer["currentVersion"]?.GetValue<string>();
        var previousVersion = pointer["previousVersion"]?.GetValue<string>();
        var previousPath = pointer["previousPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(previousVersion) || string.IsNullOrWhiteSpace(previousPath) ||
            !IsUnderRoot(previousPath, Path.Combine(installRoot, "releases")) || !Directory.Exists(previousPath))
        {
            return Reject(request, "rollback_unavailable", "上一版本路径缺失或不可信");
        }

        var previousManifestPath = Path.Combine(previousPath, ".companyops", "release-manifest.json");
        if (!File.Exists(previousManifestPath))
        {
            return Reject(request, "rollback_unavailable", "上一版本缺少受控 ReleaseManifest");
        }

        var previousManifest = JsonNode.Parse(
            await File.ReadAllTextAsync(previousManifestPath, cancellationToken))?.AsObject();
        if (previousManifest?["metadata"]?["projectId"]?.GetValue<string>() != request.ProjectId ||
            previousManifest["metadata"]?["version"]?.GetValue<string>() != previousVersion)
        {
            return Reject(request, "rollback_manifest_conflict", "上一版本 ReleaseManifest 与 pointer 不一致");
        }

        using var lease = operationGate.TryAcquire([$"project:{request.ProjectId}:{request.Environment}"]);
        if (lease is null)
        {
            return Reject(request, "resource_busy", "项目正在执行其他操作");
        }

        var activation = await activator.ActivateAsync(
            request.ProjectId,
            request.Environment,
            previousPath,
            cancellationToken);
        if (!activation.Success)
        {
            return Reject(request, "rollback_activation_failed", activation.Detail ?? "回滚激活失败");
        }

        var context = new DeploymentContext(
            binding,
            installRoot,
            previousPath,
            string.Empty,
            currentVersion,
            [],
            null);
        await WriteReleasePointerAsync(context, previousVersion, cancellationToken, currentVersion);
        await WriteInstalledStateAsync(
            request with { ReleaseManifestPath = previousManifestPath },
            context,
            previousVersion,
            cancellationToken);
        var result = Success(
            request,
            currentVersion,
            previousVersion,
            ["上一不可变 release 存在", "激活器确认成功", "release pointer 已原子回拨"]);
        await AuditAsync(result, cancellationToken);
        return result;
    }

    private async Task<DeploymentContext> LoadContextAsync(
        DeploymentRequest request,
        string version,
        CancellationToken cancellationToken)
    {
        var binding = await FindBindingAsync(request.ProjectId, request.Environment, cancellationToken);
        if (binding is null)
        {
            return DeploymentContext.Fail("binding_not_unique", "无法取得唯一 EnvironmentBinding");
        }

        var project = snapshotCache.Read().Projects?.Projects.SingleOrDefault(
            item => item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        if (project is null || project.Status == ProjectBindingStatus.Conflict ||
            (project.Generation ?? 0) != request.ExpectedGeneration)
        {
            return DeploymentContext.Fail("generation_or_ownership_conflict", "项目归属冲突或 generation 已变化");
        }

        var installRoot = Path.GetFullPath(binding["roots"]!["install"]!.GetValue<string>());
        var releasePath = Path.GetFullPath(Path.Combine(installRoot, "releases", version));
        var stagingPath = Path.GetFullPath(Path.Combine(installRoot, ".staging", request.OperationId));
        if (!SafeSegment(request.OperationId) || !IsUnderRoot(releasePath, installRoot) || !IsUnderRoot(stagingPath, installRoot))
        {
            return DeploymentContext.Fail("unsafe_install_path", "安装目录或操作 ID 不安全");
        }

        var ports = binding["portBindings"]?.AsArray().OfType<JsonObject>()
            .Select(item => new PortReservationRequest(
                item["protocol"]!.GetValue<string>(),
                item["address"]!.GetValue<string>(),
                item["port"]!.GetValue<int>(),
                request.ProjectId,
                request.Environment,
                item["componentId"]!.GetValue<string>(),
                item["portId"]!.GetValue<string>(),
                request.OperationId))
            .ToArray() ?? [];
        return new DeploymentContext(binding, installRoot, releasePath, stagingPath, project.InstalledVersion, ports, null);
    }

    private async Task<JsonObject?> FindBindingAsync(
        string projectId,
        string environment,
        CancellationToken cancellationToken)
    {
        var matches = new List<JsonObject>();
        foreach (var entry in snapshotCache.Read().Catalog?.Entries.Where(
                     item => item.IsValid && item.ManifestKind == "EnvironmentBinding" && item.ProjectId == projectId) ?? [])
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken)) as JsonObject;
            if (root?["metadata"]?["environment"]?.GetValue<string>() == environment &&
                root["metadata"]?["hostId"]?.GetValue<string>() == _paths.HostId)
            {
                matches.Add(root);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static async Task ValidatePayloadsAsync(
        string manifestPath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        foreach (var payload in root["componentPayloads"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var artifactId = payload["artifactId"]!.GetValue<string>();
            var relativePath = payload["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar);
            var artifactRoot = Path.Combine(stagingPath, artifactId);
            var fullPath = Path.GetFullPath(Path.Combine(artifactRoot, relativePath));
            if (!IsUnderRoot(fullPath, artifactRoot) || (!File.Exists(fullPath) && !Directory.Exists(fullPath)))
            {
                throw new InvalidDataException($"componentPayload 不存在或路径逃逸：{artifactId}/{relativePath}");
            }
        }
    }

    private async Task WriteInstalledStateAsync(
        DeploymentRequest request,
        DeploymentContext context,
        string version,
        CancellationToken cancellationToken)
    {
        var projectEntry = snapshotCache.Read().Catalog?.Entries.Single(
            item => item.IsValid && item.ManifestKind == "ProjectManifest" && item.ProjectId == request.ProjectId);
        var project = JsonNode.Parse(await File.ReadAllTextAsync(projectEntry!.Path, cancellationToken))!.AsObject();
        var runtimeProject = snapshotCache.Read().Projects?.Projects.SingleOrDefault(item =>
            item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        var bindings = context.Binding["componentBindings"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(static item => item["componentId"]!.GetValue<string>(), StringComparer.Ordinal);
        var components = new JsonArray();
        foreach (var component in project["components"]!.AsArray().OfType<JsonObject>())
        {
            var id = component["id"]!.GetValue<string>();
            var kind = component["kind"]!.GetValue<string>();
            var adapter = kind switch
            {
                "windowsService" => "scm",
                "iisSite" or "staticSite" => "iis",
                "scheduledTask" => "taskScheduler",
                _ => "pm2Legacy"
            };
            components.Add(new JsonObject
            {
                ["componentId"] = id,
                ["kind"] = kind,
                ["adapter"] = adapter,
                ["nativeId"] = kind == "pm2Legacy"
                    ? runtimeProject?.Components.SingleOrDefault(item => item.ComponentId == id)?.InstalledNativeId
                      ?? $"unbound:{bindings[id]["nativeName"]!.GetValue<string>()}"
                    : bindings[id]["nativeName"]!.GetValue<string>(),
                ["runtimeState"] = "unknown",
                ["healthState"] = "unknown"
            });
        }

        await using var manifestStream = File.OpenRead(request.ReleaseManifestPath!);
        var manifestHash = Convert.ToHexString(await SHA256.HashDataAsync(manifestStream, cancellationToken)).ToLowerInvariant();
        var state = new JsonObject
        {
            ["$schema"] = "../spec/v1/schemas/installed-state.schema.json",
            ["apiVersion"] = "ops.company/v1",
            ["manifestKind"] = "InstalledState",
            ["metadata"] = new JsonObject
            {
                ["projectId"] = request.ProjectId,
                ["environment"] = request.Environment,
                ["hostId"] = _paths.HostId,
                ["generation"] = request.ExpectedGeneration + 1,
                ["observedAt"] = DateTimeOffset.UtcNow.ToString("O")
            },
            ["release"] = new JsonObject
            {
                ["version"] = version,
                ["releaseManifestSha256"] = manifestHash,
                ["currentPath"] = context.ReleasePath,
                ["previousVersion"] = context.CurrentVersion,
                ["previousPath"] = context.CurrentVersion is null ? null : Path.Combine(context.InstallRoot, "releases", context.CurrentVersion),
                ["installedAt"] = DateTimeOffset.UtcNow.ToString("O")
            },
            ["components"] = components,
            ["lastOperation"] = new JsonObject
            {
                ["operationId"] = request.OperationId,
                ["action"] = request.Action switch
                {
                    DeploymentAction.Install => "install",
                    DeploymentAction.Rollback => "rollback",
                    _ => "update"
                },
                ["status"] = "succeeded",
                ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["completedAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };
        if (context.CurrentVersion is null)
        {
            state["release"]!.AsObject().Remove("previousVersion");
            state["release"]!.AsObject().Remove("previousPath");
        }

        Directory.CreateDirectory(_paths.ManifestDirectory);
        var destination = Path.Combine(
            _paths.ManifestDirectory,
            $"{request.ProjectId}.{request.Environment}.{_paths.HostId}.installed-state.json");
        var temporary = destination + $".{request.OperationId}.tmp";
        await File.WriteAllTextAsync(temporary, state.ToJsonString(jsonOptions), cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task WriteReleasePointerAsync(
        DeploymentContext context,
        string version,
        CancellationToken cancellationToken,
        string? previousOverride = null)
    {
        var pointerPath = Path.Combine(context.InstallRoot, "current.release.json");
        var pointer = new JsonObject
        {
            ["currentVersion"] = version,
            ["currentPath"] = context.ReleasePath,
            ["previousVersion"] = previousOverride ?? context.CurrentVersion,
            ["previousPath"] = (previousOverride ?? context.CurrentVersion) is { } previous
                ? Path.Combine(context.InstallRoot, "releases", previous)
                : null,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        var temporary = pointerPath + ".tmp";
        await File.WriteAllTextAsync(temporary, pointer.ToJsonString(), cancellationToken);
        File.Move(temporary, pointerPath, overwrite: true);
    }

    private static Task QuarantineFailedReleaseAsync(DeploymentContext context, string operationId)
    {
        var source = Directory.Exists(context.ReleasePath) ? context.ReleasePath : context.StagingPath;
        if (!Directory.Exists(source))
        {
            return Task.CompletedTask;
        }

        var failedRoot = Path.Combine(context.InstallRoot, ".failed");
        Directory.CreateDirectory(failedRoot);
        var destination = Path.Combine(failedRoot, operationId);
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
        }

        return Task.CompletedTask;
    }

    private async Task AuditAsync(DeploymentResult result, CancellationToken cancellationToken) =>
        await stateStore.AppendAuditEventAsync(
            new AuditEvent(
                Guid.CreateVersion7().ToString(),
                DateTimeOffset.UtcNow,
                "deployment",
                result.Action.ToString(),
                result.Outcome.ToString(),
                $"{result.ProjectId}/{result.Environment}: {result.FromVersion} -> {result.ToVersion}; {result.ErrorCode ?? result.Detail}"),
            cancellationToken);

    private static bool SafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsUnderRoot(string path, string root)
    {
        var resolvedPath = Path.GetFullPath(path);
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static DeploymentResult Success(
        DeploymentRequest request,
        string? from,
        string? to,
        IReadOnlyList<string> steps) =>
        new(request.OperationId, request.Action, OperationOutcome.Succeeded, request.ProjectId, request.Environment, from, to, steps);

    private static DeploymentResult Reject(
        DeploymentRequest request,
        string code,
        string detail,
        IReadOnlyList<string>? steps = null) =>
        new(request.OperationId, request.Action, OperationOutcome.Rejected, request.ProjectId, request.Environment, null, null, steps ?? [], code, detail);

    private sealed record DeploymentContext(
        JsonObject Binding,
        string InstallRoot,
        string ReleasePath,
        string StagingPath,
        string? CurrentVersion,
        IReadOnlyList<PortReservationRequest> Ports,
        (string Code, string Detail)? Error)
    {
        public static DeploymentContext Fail(string code, string detail) =>
            new(new JsonObject(), string.Empty, string.Empty, string.Empty, null, [], (code, detail));
    }

    private sealed record IdempotentDeployment(
        string Fingerprint,
        Lazy<Task<DeploymentResult>> Execution);
}
