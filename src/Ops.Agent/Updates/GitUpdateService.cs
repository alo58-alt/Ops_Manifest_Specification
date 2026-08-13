using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Updates;

public sealed class GitUpdateService
{
    private static readonly TimeSpan LocalGitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkGitTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] DependencyFiles =
    [
        "requirements.txt", "pyproject.toml", "poetry.lock", "uv.lock",
        "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock",
        "global.json", "Directory.Packages.props"
    ];

    private readonly AgentSnapshotCache _snapshotCache;
    private readonly IOpsStateStore _stateStore;
    private readonly OperationGate _gate;
    private readonly IOperationSnapshotRefresher _snapshotRefresher;
    private readonly IComponentHealthGate _healthGate;
    private readonly IReadOnlyDictionary<string, IComponentControlAdapter> _controlAdapters;
    private readonly IGitCommandRunner _git;
    private readonly IGitCredentialStore _credentials;
    private readonly OpsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, IdempotentGitUpdate> _operations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _operationStartedAt =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _credentialFingerprints =
        new(StringComparer.Ordinal);

    public GitUpdateService(
        AgentSnapshotCache snapshotCache,
        IOpsStateStore stateStore,
        OperationGate gate,
        IOperationSnapshotRefresher snapshotRefresher,
        IComponentHealthGate healthGate,
        IEnumerable<IComponentControlAdapter> adapters,
        IGitCommandRunner git,
        IGitCredentialStore credentials,
        IOptions<OpsOptions> options,
        JsonSerializerOptions jsonOptions)
    {
        _snapshotCache = snapshotCache;
        _stateStore = stateStore;
        _gate = gate;
        _snapshotRefresher = snapshotRefresher;
        _healthGate = healthGate;
        _controlAdapters = adapters.ToDictionary(static adapter => adapter.Kind, StringComparer.Ordinal);
        _git = git;
        _credentials = credentials;
        _options = options.Value;
        _jsonOptions = jsonOptions;
    }

    public Task<GitUpdateResult> ExecuteAsync(
        GitUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Task.FromResult(Reject(request, "invalid_git_update", validationError));
        }

        var fingerprint = JsonSerializer.Serialize(
            request with { IdempotencyKey = string.Empty },
            _jsonOptions);
        var candidate = new IdempotentGitUpdate(
            fingerprint,
            new Lazy<Task<GitUpdateResult>>(
                () => ExecuteCoreAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _operations.GetOrAdd(request.IdempotencyKey, candidate);
        return string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? operation.Execution.Value
            : Task.FromResult(Reject(
                request,
                "idempotency_conflict",
                "同一 IdempotencyKey 已用于不同 Git 更新请求"));
    }

    public async Task<GitCredentialSetResult> SetCredentialAsync(
        GitCredentialSetRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.Environment) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Secret) ||
            request.Username.Length > 256 ||
            request.Secret.Length > 4096)
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "invalid_git_credential", "凭据参数不完整或长度无效"),
                cancellationToken);
        }

        var credentialFingerprint = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(
                "\n",
                request.OperationId,
                request.ProjectId,
                request.Environment,
                request.ExpectedGeneration,
                request.Username,
                request.Secret))));
        var knownFingerprint = _credentialFingerprints.GetOrAdd(
            request.IdempotencyKey,
            credentialFingerprint);
        if (!string.Equals(knownFingerprint, credentialFingerprint, StringComparison.Ordinal))
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "idempotency_conflict", "同一 IdempotencyKey 已用于不同凭据请求"),
                cancellationToken);
        }

        await _snapshotRefresher.RefreshAsync(cancellationToken);
        var snapshot = _snapshotCache.Read();
        var project = snapshot.Projects?.Projects.SingleOrDefault(item =>
            item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        if (project is null ||
            project.Generation != request.ExpectedGeneration ||
            project.Status == ProjectBindingStatus.Conflict ||
            !project.GitUpdateEnabled)
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "ownership_not_proven", "项目环境、generation 或 Git 更新归属校验失败"),
                cancellationToken);
        }

        var manifestEntry = snapshot.Catalog?.Entries.SingleOrDefault(entry =>
            entry.IsValid && entry.ManifestKind == "ProjectManifest" &&
            entry.ProjectId == request.ProjectId);
        if (manifestEntry is null)
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "manifest_not_unique", "无法取得唯一 ProjectManifest"),
                cancellationToken);
        }

        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(manifestEntry.Path, cancellationToken))?.AsObject();
        var source = manifest?["update"]?["source"]?.AsObject();
        if (source?["kind"]?.GetValue<string>() != "gitFastForward")
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "git_update_not_declared", "项目没有声明 gitFastForward 更新来源"),
                cancellationToken);
        }

        var expectedRemoteUrl = source["remoteUrl"]?.GetValue<string>() ?? string.Empty;
        string normalizedRemote;
        try
        {
            normalizedRemote = GitCredentialStore.NormalizeRemoteUrl(expectedRemoteUrl);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UriFormatException)
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "remote_not_eligible", exception.Message),
                cancellationToken);
        }

        var repositoryRoot = ResolveRepositoryRoot(project.InstallRoot);
        var remote = source["remote"]?.GetValue<string>() ?? string.Empty;
        if (repositoryRoot is null || string.IsNullOrWhiteSpace(remote))
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "repository_invalid", "项目安装目录不是可用的独立 Git 仓库"),
                cancellationToken);
        }
        var actualRemote = await _git.RunAsync(
            repositoryRoot,
            ["remote", "get-url", remote],
            LocalGitTimeout,
            cancellationToken);
        if (!actualRemote.Success || !UrlsEqual(actualRemote.StandardOutput.Trim(), expectedRemoteUrl))
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "remote_mismatch", "服务器仓库远端与项目声明不一致"),
                cancellationToken);
        }

        try
        {
            _credentials.Save(expectedRemoteUrl, request.Username.Trim(), request.Secret);
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or
            PlatformNotSupportedException or InvalidOperationException)
        {
            return await AuditCredentialAsync(
                CredentialFailure(request, "credential_storage_failed", exception.Message),
                cancellationToken);
        }

        var host = new Uri(normalizedRemote).IdnHost;
        return await AuditCredentialAsync(
            new GitCredentialSetResult(
                request.OperationId,
                OperationOutcome.Succeeded,
                request.ProjectId,
                request.Environment,
                host,
                true,
                Detail: $"已为 {host} 安全保存主机凭据；令牌未写入项目声明或 Git URL。"),
            cancellationToken);
    }

    private async Task<GitUpdateResult> ExecuteCoreAsync(
        GitUpdateRequest request,
        CancellationToken cancellationToken)
    {
        _operationStartedAt.TryAdd(request.OperationId, DateTimeOffset.UtcNow);
        if (!_options.EnableExistingGitUpdates)
        {
            return await AuditAsync(
                Reject(request, "git_updates_disabled", "Agent 未启用已有项目 Git 更新"),
                cancellationToken);
        }

        await _snapshotRefresher.RefreshAsync(cancellationToken);
        var snapshot = _snapshotCache.Read();
        var project = snapshot.Projects?.Projects.SingleOrDefault(item =>
            item.ProjectId == request.ProjectId && item.Environment == request.Environment);
        if (project is null)
        {
            return await AuditAsync(
                Reject(request, "project_not_found", "未找到唯一项目环境"),
                cancellationToken);
        }
        if (project.Status == ProjectBindingStatus.Conflict ||
            project.Generation != request.ExpectedGeneration ||
            project.Components.Count == 0 ||
            project.Components.Any(component =>
                component.Kind is not ("windowsService" or "interactiveApp") ||
                component.Ownership != ComponentOwnershipStatus.Owned))
        {
            return await AuditAsync(
                Reject(request, "ownership_not_proven", "Git 更新仅允许全部组件都已精确归属的 Windows Service 或用户会话程序"),
                cancellationToken);
        }

        var manifestEntry = snapshot.Catalog?.Entries.SingleOrDefault(entry =>
            entry.IsValid && entry.ManifestKind == "ProjectManifest" &&
            entry.ProjectId == request.ProjectId);
        if (manifestEntry is null)
        {
            return await AuditAsync(
                Reject(request, "manifest_not_unique", "无法取得唯一 ProjectManifest"),
                cancellationToken);
        }

        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(manifestEntry.Path, cancellationToken))?.AsObject();
        if (manifest?["update"]?["rollbackOnFailure"]?.GetValue<bool>() != true)
        {
            return await AuditAsync(
                Reject(request, "rollback_required", "L3 Git 更新要求 rollbackOnFailure=true"),
                cancellationToken);
        }
        var source = manifest?["update"]?["source"]?.AsObject();
        if (source?["kind"]?.GetValue<string>() != "gitFastForward")
        {
            return await AuditAsync(
                Reject(request, "git_update_not_declared", "项目没有声明 gitFastForward 更新来源"),
                cancellationToken);
        }

        var repositoryRoot = ResolveRepositoryRoot(project.InstallRoot);
        if (repositoryRoot is null)
        {
            return await AuditAsync(
                Reject(request, "repository_invalid", "项目安装目录不是可用的独立 Git 仓库"),
                cancellationToken);
        }

        var remote = source["remote"]!.GetValue<string>();
        var branch = source["branch"]!.GetValue<string>();
        var remoteUrl = source["remoteUrl"]!.GetValue<string>();
        var inspection = await InspectAsync(repositoryRoot, remote, branch, remoteUrl, cancellationToken);
        if (inspection.Error is not null)
        {
            return await AuditAsync(
                Reject(
                    request,
                    inspection.Error.Value.Code,
                    inspection.Error.Value.Detail,
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles),
                cancellationToken);
        }

        if (request.Action == GitUpdateAction.Check)
        {
            return await AuditAsync(
                new GitUpdateResult(
                    request.OperationId,
                    request.Action,
                    OperationOutcome.Succeeded,
                    request.ProjectId,
                    request.Environment,
                    inspection.UpdateAvailable,
                    inspection.CanApply,
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles,
                    inspection.Steps,
                    Detail: inspection.UpdateAvailable
                        ? inspection.CanApply
                            ? "发现可安全快进的更新，请确认提交号后执行。"
                            : "发现更新，但包含依赖清单变化；请先生成 ReleaseManifest 制品发布。"
                        : "当前已经是声明远端分支的最新提交。"),
                cancellationToken);
        }

        if (!inspection.UpdateAvailable)
        {
            return await AuditAsync(
                Reject(
                    request,
                    "already_current",
                    "当前已是最新版本，无需更新",
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles),
                cancellationToken);
        }
        if (!inspection.CanApply)
        {
            return await AuditAsync(
                Reject(
                    request,
                    "dependency_change_requires_release",
                    "更新包含依赖清单变化；必须走 ReleaseManifest 制品发布，禁止在生产目录现场安装依赖",
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles),
                cancellationToken);
        }
        if (!string.Equals(request.ExpectedCurrentCommit, inspection.CurrentCommit, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedRemoteCommit, inspection.RemoteCommit, StringComparison.Ordinal))
        {
            return await AuditAsync(
                Reject(
                    request,
                    "git_plan_changed",
                    "本地或远端提交已变化，请重新检查更新",
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles),
                cancellationToken);
        }

        using var lease = _gate.TryAcquire(
            [$"project:{request.ProjectId}:{request.Environment}", .. project.Components.Select(static item => item.ExpectedNativeId)]);
        if (lease is null)
        {
            return await AuditAsync(
                Reject(request, "resource_busy", "项目或服务正在执行其他操作"),
                cancellationToken);
        }

        // Re-check under the resource gate so another operation cannot invalidate the plan.
        var finalInspection = await InspectAsync(repositoryRoot, remote, branch, remoteUrl, cancellationToken);
        if (finalInspection.Error is not null ||
            !string.Equals(finalInspection.CurrentCommit, inspection.CurrentCommit, StringComparison.Ordinal) ||
            !string.Equals(finalInspection.RemoteCommit, inspection.RemoteCommit, StringComparison.Ordinal) ||
            !finalInspection.CanApply)
        {
            return await AuditAsync(
                Reject(request, "git_plan_changed", "取得操作门禁后状态已变化，请重新检查更新"),
                cancellationToken);
        }

        var steps = inspection.Steps.ToList();
        steps.Add($"提交计划：{inspection.CurrentCommit} -> {inspection.RemoteCommit}");
        steps.Add($"变更文件：{inspection.ChangedFiles.Count} 个");
        var runningBefore = project.Components
            .Where(component => component.RuntimeState == "running")
            .ToArray();
        var stopped = new List<ProjectComponentRuntimeView>();
        try
        {
            foreach (var component in runningBefore.Reverse())
            {
                var stop = await ControlAsync(
                    ToTarget(project, component),
                    ComponentOperationAction.Stop,
                    cancellationToken);
                steps.Add($"停止 {component.ComponentId}：{stop.Detail}");
                if (!stop.Success)
                {
                    return await FailAndRestoreServicesAsync(
                        request, project, stopped, inspection, steps,
                        "service_stop_failed", stop.Detail, cancellationToken);
                }
                stopped.Add(component);
            }

            var merge = await _git.RunAsync(
                repositoryRoot,
                ["merge", "--ff-only", $"refs/remotes/{remote}/{branch}"],
                LocalGitTimeout,
                cancellationToken);
            steps.Add($"Git 快进：{merge.Detail}");
            if (!merge.Success)
            {
                return await FailAndRestoreServicesAsync(
                    request, project, stopped, inspection, steps,
                    "git_fast_forward_failed", merge.Detail, cancellationToken);
            }
            steps.Add($"已将工作树快进到 {inspection.RemoteCommit}");

            foreach (var component in runningBefore)
            {
                var start = await ControlAsync(
                    ToTarget(project, component),
                    ComponentOperationAction.Start,
                    cancellationToken);
                steps.Add($"启动 {component.ComponentId}：{start.Detail}");
                if (!start.Success)
                {
                    return await RollbackAsync(
                        request, repositoryRoot, remote, branch, project, runningBefore,
                        inspection, steps, "service_start_failed", start.Detail, cancellationToken);
                }

                var health = await WaitForHealthAsync(
                    request.ProjectId,
                    request.Environment,
                    component.ComponentId,
                    TimeSpan.FromSeconds(Math.Clamp(
                        manifest!["update"]?["healthTimeoutSeconds"]?.GetValue<int>() ?? 60,
                        5,
                        600)),
                    cancellationToken);
                steps.Add($"健康 {component.ComponentId}：{health.Detail}");
                if (!health.Success)
                {
                    return await RollbackAsync(
                        request, repositoryRoot, remote, branch, project, runningBefore,
                        inspection, steps, "health_gate_failed", health.Detail, cancellationToken);
                }
            }

            await _snapshotRefresher.RefreshAsync(cancellationToken);
            steps.Add("已刷新项目运行状态");
            return await AuditAsync(
                new GitUpdateResult(
                    request.OperationId,
                    request.Action,
                    OperationOutcome.Succeeded,
                    request.ProjectId,
                    request.Environment,
                    false,
                    false,
                    inspection.CurrentCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles,
                    steps,
                    Detail: "Git 快进更新完成，原运行组件已恢复并通过健康检查。"),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await RollbackAsync(
                request, repositoryRoot, remote, branch, project, runningBefore,
                inspection, steps, "git_update_timeout", "更新执行超时", CancellationToken.None);
        }
    }

    private async Task<HealthGateResult> WaitForHealthAsync(
        string projectId,
        string environment,
        string componentId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        HealthGateResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _healthGate.ProbeAsync(
                projectId,
                environment,
                componentId,
                cancellationToken);
            if (last.Success)
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return last ?? new HealthGateResult(false, "健康检查超时");
    }

    private async Task<GitUpdateResult> FailAndRestoreServicesAsync(
        GitUpdateRequest request,
        ProjectRuntimeView project,
        IReadOnlyList<ProjectComponentRuntimeView> stopped,
        GitInspection inspection,
        List<string> steps,
        string errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        foreach (var component in stopped)
        {
            var start = await ControlAsync(
                ToTarget(project, component), ComponentOperationAction.Start, cancellationToken);
            steps.Add($"恢复启动 {component.ComponentId}：{start.Detail}");
        }
        return await AuditAsync(
            Failed(request, errorCode, detail, inspection, steps),
            cancellationToken);
    }

    private async Task<GitUpdateResult> RollbackAsync(
        GitUpdateRequest request,
        string repositoryRoot,
        string remote,
        string branch,
        ProjectRuntimeView project,
        IReadOnlyList<ProjectComponentRuntimeView> runningBefore,
        GitInspection inspection,
        List<string> steps,
        string errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        foreach (var component in runningBefore.Reverse())
        {
            var stop = await ControlAsync(
                ToTarget(project, component), ComponentOperationAction.Stop, cancellationToken);
            steps.Add($"回滚前停止 {component.ComponentId}：{stop.Detail}");
        }

        var rollback = await _git.RunAsync(
            repositoryRoot,
            ["reset", "--keep", inspection.CurrentCommit!],
            LocalGitTimeout,
            cancellationToken);
        steps.Add($"回滚到 {Short(inspection.CurrentCommit)}：{rollback.Detail}");
        foreach (var component in runningBefore)
        {
            var start = await ControlAsync(
                ToTarget(project, component), ComponentOperationAction.Start, cancellationToken);
            steps.Add($"回滚后启动 {component.ComponentId}：{start.Detail}");
        }
        await _snapshotRefresher.RefreshAsync(cancellationToken);
        return await AuditAsync(
            Failed(
                request,
                errorCode,
                $"{detail}；{(rollback.Success ? "已恢复原提交" : $"回滚失败：{rollback.Detail}")}",
                inspection,
                steps),
            cancellationToken);
    }

    private async Task<GitInspection> InspectAsync(
        string repositoryRoot,
        string remote,
        string branch,
        string expectedRemoteUrl,
        CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        var status = await _git.RunAsync(
            repositoryRoot, ["status", "--porcelain=v1", "--untracked-files=all"],
            LocalGitTimeout, cancellationToken);
        if (!status.Success)
        {
            return GitInspection.Fail("git_status_failed", status.Detail);
        }
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return GitInspection.Fail("working_tree_dirty", "服务器项目存在未提交或未跟踪文件，拒绝覆盖");
        }
        steps.Add("Git 工作树干净");

        var currentBranch = await _git.RunAsync(
            repositoryRoot, ["branch", "--show-current"], LocalGitTimeout, cancellationToken);
        if (!currentBranch.Success || currentBranch.StandardOutput.Trim() != branch)
        {
            return GitInspection.Fail("branch_mismatch", $"当前分支不是声明的 {branch}");
        }
        steps.Add($"当前分支与声明一致：{branch}");

        var remoteUrl = await _git.RunAsync(
            repositoryRoot, ["remote", "get-url", remote], LocalGitTimeout, cancellationToken);
        if (!remoteUrl.Success || !UrlsEqual(remoteUrl.StandardOutput.Trim(), expectedRemoteUrl))
        {
            return GitInspection.Fail("remote_mismatch", $"远端 {remote} URL 与项目声明不一致");
        }
        steps.Add($"远端来源与声明一致：{remote}");

        var current = await _git.RunAsync(
            repositoryRoot, ["rev-parse", "HEAD"], LocalGitTimeout, cancellationToken);
        if (!current.Success || !ValidCommit(current.StandardOutput.Trim()))
        {
            return GitInspection.Fail("current_commit_unavailable", current.Detail);
        }
        var currentCommit = current.StandardOutput.Trim();

        var credential = _credentials.Find(expectedRemoteUrl);
        var fetch = await _git.RunAsync(
            repositoryRoot,
            ["fetch", "--no-tags", "--prune", remote, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}"],
            NetworkGitTimeout,
            cancellationToken,
            credential);
        if (!fetch.Success)
        {
            if (IsAuthenticationFailure(fetch.Detail))
            {
                return GitInspection.Fail(
                    credential is null ? "git_credentials_required" : "git_credential_rejected",
                    credential is null
                        ? "该 HTTPS Git 远端需要身份验证，请先在项目卡片中配置仓库凭据。"
                        : "已保存的 Git 仓库凭据被远端拒绝，请重新配置有效凭据。",
                    currentCommit);
            }
            return GitInspection.Fail("git_fetch_failed", fetch.Detail, currentCommit);
        }
        steps.Add("远端分支读取成功（未修改当前代码）");

        var remoteRef = $"refs/remotes/{remote}/{branch}";
        var remoteCommitResult = await _git.RunAsync(
            repositoryRoot, ["rev-parse", remoteRef], LocalGitTimeout, cancellationToken);
        if (!remoteCommitResult.Success || !ValidCommit(remoteCommitResult.StandardOutput.Trim()))
        {
            return GitInspection.Fail("remote_commit_unavailable", remoteCommitResult.Detail, currentCommit);
        }
        var remoteCommit = remoteCommitResult.StandardOutput.Trim();
        if (currentCommit == remoteCommit)
        {
            return new GitInspection(
                false, false, currentCommit, remoteCommit, [], steps, null);
        }

        var ancestor = await _git.RunAsync(
            repositoryRoot, ["merge-base", "--is-ancestor", currentCommit, remoteCommit],
            LocalGitTimeout, cancellationToken);
        if (!ancestor.Success)
        {
            return GitInspection.Fail(
                "non_fast_forward", "本地与远端已经分叉，拒绝自动覆盖", currentCommit, remoteCommit);
        }
        steps.Add("远端可由当前提交安全快进");

        var diff = await _git.RunAsync(
            repositoryRoot,
            ["diff", "--name-only", "--diff-filter=ACDMRTUXB", $"{currentCommit}..{remoteCommit}"],
            LocalGitTimeout,
            cancellationToken);
        if (!diff.Success)
        {
            return GitInspection.Fail("git_diff_failed", diff.Detail, currentCommit, remoteCommit);
        }
        var changedFiles = diff.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToArray();
        var dependencyChanges = changedFiles
            .Where(IsDependencyFile)
            .ToArray();
        if (dependencyChanges.Length > 0)
        {
            steps.Add($"依赖清单发生变化：{string.Join(", ", dependencyChanges)}");
        }

        var frontendSourceChanged = changedFiles.Any(path =>
            path.Replace('\\', '/').StartsWith("frontend/src/", StringComparison.OrdinalIgnoreCase));
        var frontendArtifactChanged = changedFiles.Any(path =>
            path.Replace('\\', '/').StartsWith("frontend/dist/", StringComparison.OrdinalIgnoreCase));
        if (frontendSourceChanged && !frontendArtifactChanged)
        {
            steps.Add("前端源码变化但没有随提交交付 frontend/dist 构建产物");
        }

        return new GitInspection(
            true,
            dependencyChanges.Length == 0 && (!frontendSourceChanged || frontendArtifactChanged),
            currentCommit,
            remoteCommit,
            changedFiles,
            steps,
            null);
    }

    private static ComponentControlTarget ToTarget(
        ProjectRuntimeView project,
        ProjectComponentRuntimeView component) =>
        new(
            project.ProjectId,
            project.Environment,
            component.ComponentId,
            component.Kind,
            component.InstalledNativeId!,
            project.InstallRoot);

    private Task<AdapterExecutionResult> ControlAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken) =>
        _controlAdapters.TryGetValue(target.Kind, out var adapter)
            ? adapter.ExecuteAsync(target, action, cancellationToken)
            : Task.FromResult(new AdapterExecutionResult(false, $"缺少 {target.Kind} 控制适配器"));

    private async Task<GitUpdateResult> AuditAsync(
        GitUpdateResult result,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var startedAt = _operationStartedAt.TryRemove(result.OperationId, out var observedStartedAt)
            ? observedStartedAt
            : completedAt;
        var auditData = JsonSerializer.SerializeToElement(
            new GitUpdateAuditData(
                result.OperationId,
                result.ProjectId,
                result.Environment,
                result.Action,
                result.CurrentCommit,
                result.RemoteCommit,
                result.ChangedFiles,
                result.Steps,
                Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
                result.Steps.Any(step => step.Contains("回滚", StringComparison.Ordinal)),
                result.ErrorCode),
            _jsonOptions);
        await _stateStore.AppendAuditEventAsync(
            new AuditEvent(
                Guid.CreateVersion7().ToString(),
                completedAt,
                "git-update",
                result.Action.ToString(),
                result.Outcome.ToString(),
                $"{result.ProjectId}/{result.Environment}: {result.ErrorCode ?? result.Detail}",
                auditData),
            cancellationToken);
        return result;
    }

    private async Task<GitCredentialSetResult> AuditCredentialAsync(
        GitCredentialSetResult result,
        CancellationToken cancellationToken)
    {
        await _stateStore.AppendAuditEventAsync(
            new AuditEvent(
                Guid.CreateVersion7().ToString(),
                DateTimeOffset.UtcNow,
                "git-credential",
                "set",
                result.Outcome.ToString(),
                $"{result.ProjectId}/{result.Environment}: {result.ErrorCode ?? result.Detail}"),
            cancellationToken);
        return result;
    }

    private static GitCredentialSetResult CredentialFailure(
        GitCredentialSetRequest request,
        string errorCode,
        string detail) =>
        new(
            request.OperationId,
            OperationOutcome.Rejected,
            request.ProjectId,
            request.Environment,
            null,
            false,
            errorCode,
            detail);

    private static bool IsAuthenticationFailure(string detail) =>
        detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("HTTP Basic: Access denied", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("askpass", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("credential", StringComparison.OrdinalIgnoreCase) &&
        detail.Contains("failed", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveRepositoryRoot(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
            return Directory.Exists(root) && Directory.Exists(Path.Combine(root, ".git"))
                ? root
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDependencyFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return DependencyFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool UrlsEqual(string actual, string expected) =>
        string.Equals(
            actual.Trim().TrimEnd('/'),
            expected.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool ValidCommit(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static string Short(string? commit) =>
        commit is { Length: >= 12 } ? commit[..12] : commit ?? "unknown";

    private static string? ValidateRequest(GitUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.Environment) ||
            request.ExpectedGeneration < 1)
        {
            return "操作标识、项目、环境和 generation 必须完整且在范围内";
        }
        if (request.Action == GitUpdateAction.Apply &&
            (!ValidCommit(request.ExpectedCurrentCommit ?? string.Empty) ||
             !ValidCommit(request.ExpectedRemoteCommit ?? string.Empty)))
        {
            return "执行更新必须携带检查阶段返回的完整提交号";
        }
        return null;
    }

    private static GitUpdateResult Reject(
        GitUpdateRequest request,
        string code,
        string detail,
        string? currentCommit = null,
        string? remoteCommit = null,
        IReadOnlyList<string>? changedFiles = null) =>
        new(
            request.OperationId,
            request.Action,
            OperationOutcome.Rejected,
            request.ProjectId,
            request.Environment,
            false,
            false,
            currentCommit,
            remoteCommit,
            changedFiles ?? [],
            [],
            code,
            detail);

    private static GitUpdateResult Failed(
        GitUpdateRequest request,
        string code,
        string? detail,
        GitInspection inspection,
        IReadOnlyList<string> steps) =>
        new(
            request.OperationId,
            request.Action,
            OperationOutcome.Failed,
            request.ProjectId,
            request.Environment,
            true,
            false,
            inspection.CurrentCommit,
            inspection.RemoteCommit,
            inspection.ChangedFiles,
            steps,
            code,
            detail);

    private sealed record IdempotentGitUpdate(
        string Fingerprint,
        Lazy<Task<GitUpdateResult>> Execution);


    private sealed record GitInspection(
        bool UpdateAvailable,
        bool CanApply,
        string? CurrentCommit,
        string? RemoteCommit,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> Steps,
        (string Code, string Detail)? Error)
    {
        public static GitInspection Fail(
            string code,
            string detail,
            string? currentCommit = null,
            string? remoteCommit = null) =>
            new(false, false, currentCommit, remoteCommit, [], [], (code, detail));
    }
}
