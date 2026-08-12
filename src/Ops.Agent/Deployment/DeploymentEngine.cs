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
    private readonly ConcurrentDictionary<string, byte> _auditedOperations =
        new(StringComparer.Ordinal);

    public Task<DeploymentResult> ExecuteAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
        {
            return AuditStandaloneResultAsync(
                Reject(request, "invalid_idempotency_key", "IdempotencyKey 不能为空且不能超过 200 字符"));
        }

        var fingerprint = JsonSerializer.Serialize(request with { IdempotencyKey = string.Empty }, jsonOptions);
        var candidate = new IdempotentDeployment(
            fingerprint,
            new Lazy<Task<DeploymentResult>>(
                () => ExecuteAndEnsureAuditAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _deployments.GetOrAdd(request.IdempotencyKey, candidate);
        if (string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return operation.Execution.Value;
        }

        return AuditStandaloneResultAsync(Reject(
                request,
                "idempotency_conflict",
                "同一 IdempotencyKey 已用于不同部署请求"));
    }

    private async Task<DeploymentResult> AuditStandaloneResultAsync(DeploymentResult result)
    {
        using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await AppendAuditAsync(result, auditTimeout.Token);
        return result;
    }

    private async Task<DeploymentResult> ExecuteAndEnsureAuditAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(request, cancellationToken);
        if (!_auditedOperations.ContainsKey(result.OperationId))
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AuditAsync(result, auditTimeout.Token);
        }

        return result;
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
            "ProjectManifest 哈希、EnvironmentBinding、InstalledState generation 与项目归属校验通过",
            $"计划目标：{context.ReleasePath}"
        };
        var activationRequest = new DeploymentActivationRequest(
            request.ProjectId,
            request.Environment,
            context.ReleasePath,
            context.ProjectManifest,
            context.ReleaseManifest,
            context.Binding);
        var activationPlan = await activator.PlanAsync(activationRequest, cancellationToken);
        if (!activationPlan.Success)
        {
            return Reject(
                request,
                "activation_not_supported",
                activationPlan.Detail ?? "发布缺少受控激活能力",
                steps);
        }

        steps.AddRange(activationPlan.Steps ?? []);
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
                .Concat(context.Ports.Select(static port => $"port:{port.Protocol}:{port.Address}:{port.Port}"))
                .Concat(context.Binding["componentBindings"]!.AsArray().OfType<JsonObject>()
                    .Select(static item => item["nativeName"]!.GetValue<string>())));
        if (lease is null)
        {
            return Reject(request, "resource_busy", "项目或端口资源正在执行其他操作", steps);
        }

        if (Directory.Exists(context.StagingPath) || Directory.Exists(context.ReleasePath))
        {
            return Reject(request, "release_path_exists", "staging 或目标 release 已存在，拒绝覆盖", steps);
        }

        var reservation = await portRegistry.ReserveAsync(context.Ports, cancellationToken);
        if (!reservation.Success)
        {
            return Reject(request, reservation.ErrorCode ?? "port_reservation_failed", reservation.Detail ?? "端口预留失败", steps);
        }

        steps.Add("端口批量事务预留成功");
        var stagingPath = context.StagingPath;
        DeploymentActivationResult? activation = null;
        var pointerSnapshot = CaptureFile(Path.Combine(context.InstallRoot, "current.release.json"));
        var installedStateSnapshot = CaptureFile(InstalledStatePath(request.ProjectId, request.Environment));
        try
        {
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
            File.Copy(
                context.ProjectManifestPath,
                Path.Combine(metadataDirectory, "project-manifest.json"),
                overwrite: false);
            steps.Add("staging 原子移动为不可变 release");

            activation = await activator.ActivateAsync(activationRequest, cancellationToken);
            steps.AddRange(activation.Steps ?? []);
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
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var cleanupToken = cancellationToken.IsCancellationRequested ? cleanupTimeout.Token : cancellationToken;
            var recoveryDetails = new List<string>();
            if (activation?.Rollback is not null)
            {
                var restored = await activation.Rollback.RestoreAsync(cleanupToken);
                recoveryDetails.Add($"原生入口恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}");
            }

            recoveryDetails.Add(await RestoreFileAsync(pointerSnapshot, cleanupToken));
            recoveryDetails.Add(await RestoreFileAsync(installedStateSnapshot, cleanupToken));
            await QuarantineFailedReleaseAsync(context, request.OperationId);
            await portRegistry.ReleaseOperationAsync(request.OperationId, cleanupToken);
            steps.AddRange(recoveryDetails);
            var result = Reject(
                request,
                exception is OperationCanceledException ? "deployment_cancelled" : "deployment_failed",
                $"{exception.Message}；{string.Join("；", recoveryDetails)}",
                steps);
            await AuditAsync(result, cleanupToken);
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

        if (project.Status != ProjectBindingStatus.Installed ||
            project.Components.Any(static component => component.Ownership != ComponentOwnershipStatus.Owned))
        {
            return Reject(request, "ownership_not_proven", "回滚前必须证明所有已安装原生组件的唯一归属");
        }

        var installRoot = Path.GetFullPath(binding["roots"]!["install"]!.GetValue<string>());
        if (!IsAllowedInstallRoot(installRoot))
        {
            return Reject(request, "install_root_not_allowed", "项目安装根目录不在 Agent 允许的项目父目录下");
        }

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
        var previousProjectManifestPath = Path.Combine(previousPath, ".companyops", "project-manifest.json");
        if (!File.Exists(previousManifestPath) || !File.Exists(previousProjectManifestPath))
        {
            return Reject(request, "rollback_unavailable", "上一版本缺少受控 ReleaseManifest 或 ProjectManifest");
        }

        var previousManifest = JsonNode.Parse(
            await File.ReadAllTextAsync(previousManifestPath, cancellationToken))!.AsObject();
        var previousProjectManifest = JsonNode.Parse(
            await File.ReadAllTextAsync(previousProjectManifestPath, cancellationToken))!.AsObject();
        if (previousManifest["metadata"]?["projectId"]?.GetValue<string>() != request.ProjectId ||
            previousManifest["metadata"]?["version"]?.GetValue<string>() != previousVersion)
        {
            return Reject(request, "rollback_manifest_conflict", "上一版本 ReleaseManifest 与 pointer 不一致");
        }

        await using (var projectStream = File.OpenRead(previousProjectManifestPath))
        {
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(projectStream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(
                    actualHash,
                    previousManifest["projectManifestSha256"]?.GetValue<string>(),
                    StringComparison.Ordinal))
            {
                return Reject(request, "rollback_manifest_conflict", "上一版本 ProjectManifest 哈希不可信");
            }
        }

        var activationRequest = new DeploymentActivationRequest(
            request.ProjectId,
            request.Environment,
            previousPath,
            previousProjectManifest,
            previousManifest,
            binding);
        var activationPlan = await activator.PlanAsync(activationRequest, cancellationToken);
        if (!activationPlan.Success)
        {
            return Reject(
                request,
                "rollback_activation_not_supported",
                activationPlan.Detail ?? "上一版本缺少受控激活能力");
        }

        using var lease = operationGate.TryAcquire(
            new[] { $"project:{request.ProjectId}:{request.Environment}" }
                .Concat(binding["componentBindings"]!.AsArray().OfType<JsonObject>()
                    .Select(static item => item["nativeName"]!.GetValue<string>())));
        if (lease is null)
        {
            return Reject(request, "resource_busy", "项目正在执行其他操作");
        }

        var activation = await activator.ActivateAsync(activationRequest, cancellationToken);
        if (!activation.Success)
        {
            return Reject(request, "rollback_activation_failed", activation.Detail ?? "回滚激活失败");
        }

        var context = new DeploymentContext(
            binding,
            previousProjectManifest,
            previousProjectManifestPath,
            previousManifest,
            installRoot,
            previousPath,
            string.Empty,
            currentVersion,
            [],
            null);
        var pointerSnapshot = CaptureFile(pointerPath);
        var installedStateSnapshot = CaptureFile(InstalledStatePath(request.ProjectId, request.Environment));
        try
        {
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
                new[] { "上一不可变 release 与声明哈希可信" }
                    .Concat(activation.Steps ?? [])
                    .Append("release pointer 与 InstalledState 已原子回拨")
                    .ToArray());
            await AuditAsync(result, cancellationToken);
            return result;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var cleanupToken = cancellationToken.IsCancellationRequested ? cleanupTimeout.Token : cancellationToken;
            var restored = activation.Rollback is null
                ? new DeploymentActivationResult(false, "激活器未返回可恢复事务")
                : await activation.Rollback.RestoreAsync(cleanupToken);
            var pointerRestore = await RestoreFileAsync(pointerSnapshot, cleanupToken);
            var stateRestore = await RestoreFileAsync(installedStateSnapshot, cleanupToken);
            var result = Reject(
                request,
                exception is OperationCanceledException ? "rollback_cancelled" : "rollback_state_commit_failed",
                $"{exception.Message}；原生入口恢复{(restored.Success ? "成功" : "失败")}：{restored.Detail}；{pointerRestore}；{stateRestore}",
                activation.Steps);
            await AuditAsync(result, cleanupToken);
            return result;
        }
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

        var projectManifest = await FindProjectManifestAsync(request.ProjectId, cancellationToken);
        if (projectManifest is null)
        {
            return DeploymentContext.Fail("project_manifest_not_unique", "无法取得唯一 ProjectManifest");
        }

        var releaseManifest = JsonNode.Parse(
            await File.ReadAllTextAsync(request.ReleaseManifestPath!, cancellationToken))!.AsObject();
        var expectedProjectHash = releaseManifest["projectManifestSha256"]?.GetValue<string>();
        await using (var projectStream = File.OpenRead(projectManifest.Path))
        {
            var actualProjectHash = Convert.ToHexString(
                await SHA256.HashDataAsync(projectStream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(expectedProjectHash, actualProjectHash, StringComparison.Ordinal))
            {
                return DeploymentContext.Fail(
                    "project_manifest_hash_mismatch",
                    "ReleaseManifest 绑定的 ProjectManifest SHA-256 与当前声明不一致");
            }
        }

        var project = snapshotCache.Read().Projects?.Projects.SingleOrDefault(
            item => item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        if (project is null || project.Status == ProjectBindingStatus.Conflict ||
            (project.Generation ?? 0) != request.ExpectedGeneration)
        {
            return DeploymentContext.Fail("generation_or_ownership_conflict", "项目归属冲突或 generation 已变化");
        }


        if (request.Action == DeploymentAction.Install && project.Generation is not null)
        {
            return DeploymentContext.Fail("deployment_action_conflict", "项目已有 InstalledState，必须使用 Update");
        }

        if (request.Action == DeploymentAction.Update && project.Generation is null)
        {
            return DeploymentContext.Fail("deployment_action_conflict", "项目尚未安装，必须使用 Install");
        }

        if ((request.Action == DeploymentAction.Update ||
             request.Action == DeploymentAction.Plan && project.Generation is not null) &&
            (project.Status != ProjectBindingStatus.Installed ||
             project.Components.Any(static component => component.Ownership != ComponentOwnershipStatus.Owned)))
        {
            return DeploymentContext.Fail("ownership_not_proven", "更新前必须证明所有已安装原生组件的唯一归属");
        }

        var installRoot = Path.GetFullPath(binding["roots"]!["install"]!.GetValue<string>());
        if (!IsAllowedInstallRoot(installRoot))
        {
            return DeploymentContext.Fail(
                "install_root_not_allowed",
                "项目安装根目录不在 Agent 允许的项目父目录下");
        }

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
        return new DeploymentContext(
            binding,
            projectManifest.Root,
            projectManifest.Path,
            releaseManifest,
            installRoot,
            releasePath,
            stagingPath,
            project.InstalledVersion,
            ports,
            null);
    }

    private async Task<ProjectManifestDocument?> FindProjectManifestAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var entries = snapshotCache.Read().Catalog?.Entries.Where(
            item => item.IsValid && item.ManifestKind == "ProjectManifest" && item.ProjectId == projectId).ToArray() ?? [];
        if (entries.Length != 1)
        {
            return null;
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(entries[0].Path, cancellationToken)) as JsonObject;
        return root is null ? null : new ProjectManifestDocument(entries[0].Path, root);
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
        var runtimeProject = snapshotCache.Read().Projects?.Projects.SingleOrDefault(item =>
            item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        var bindings = context.Binding["componentBindings"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(static item => item["componentId"]!.GetValue<string>(), StringComparer.Ordinal);
        var components = new JsonArray();
        foreach (var component in context.ProjectManifest["components"]!.AsArray().OfType<JsonObject>())
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
        var destination = InstalledStatePath(request.ProjectId, request.Environment);
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

    private async Task AuditAsync(DeploymentResult result, CancellationToken cancellationToken)
    {
        await AppendAuditAsync(result, cancellationToken);
        _auditedOperations.TryAdd(result.OperationId, 0);
    }

    private async Task AppendAuditAsync(DeploymentResult result, CancellationToken cancellationToken) =>
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

    private string InstalledStatePath(string projectId, string environment) =>
        Path.Combine(
            _paths.ManifestDirectory,
            $"{projectId}.{environment}.{_paths.HostId}.installed-state.json");

    private bool IsAllowedInstallRoot(string installRoot)
    {
        if (_options.AllowedProjectInstallRoots is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            var resolvedInstallRoot = Path.GetFullPath(installRoot);
            return _options.AllowedProjectInstallRoots.Any(configuredRoot =>
            {
                if (string.IsNullOrWhiteSpace(configuredRoot))
                {
                    return false;
                }

                var allowedRoot = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(configuredRoot.Trim()));
                var pathRoot = Path.GetPathRoot(allowedRoot);
                if (pathRoot is null || string.Equals(
                        Path.TrimEndingDirectorySeparator(allowedRoot),
                        Path.TrimEndingDirectorySeparator(pathRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var prefix = Path.TrimEndingDirectorySeparator(allowedRoot) + Path.DirectorySeparatorChar;
                return resolvedInstallRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static FileSnapshot CaptureFile(string path) =>
        File.Exists(path)
            ? new FileSnapshot(path, true, File.ReadAllBytes(path))
            : new FileSnapshot(path, false, null);

    private static async Task<string> RestoreFileAsync(
        FileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Existed)
        {
            if (File.Exists(snapshot.Path))
            {
                File.Delete(snapshot.Path);
            }

            return $"已移除本次新建状态文件 {Path.GetFileName(snapshot.Path)}";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
        var temporary = snapshot.Path + $".restore.{Guid.CreateVersion7():N}.tmp";
        await File.WriteAllBytesAsync(temporary, snapshot.Content!, cancellationToken);
        File.Move(temporary, snapshot.Path, overwrite: true);
        return $"已恢复原状态文件 {Path.GetFileName(snapshot.Path)}";
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
        JsonObject ProjectManifest,
        string ProjectManifestPath,
        JsonObject ReleaseManifest,
        string InstallRoot,
        string ReleasePath,
        string StagingPath,
        string? CurrentVersion,
        IReadOnlyList<PortReservationRequest> Ports,
        (string Code, string Detail)? Error)
    {
        public static DeploymentContext Fail(string code, string detail) =>
            new(
                new JsonObject(),
                new JsonObject(),
                string.Empty,
                new JsonObject(),
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                [],
                (code, detail));
    }

    private sealed record ProjectManifestDocument(string Path, JsonObject Root);

    private sealed record FileSnapshot(string Path, bool Existed, byte[]? Content);

    private sealed record IdempotentDeployment(
        string Fingerprint,
        Lazy<Task<DeploymentResult>> Execution);
}
