using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Updates;

public interface IGitBuildReleaseService
{
    Task<GitUpdateResult> ExecuteAsync(
        GitUpdateRequest request,
        ProjectRuntimeView project,
        JsonObject source,
        CancellationToken cancellationToken);
}

public sealed record ProjectReleaseBuildResult(
    bool Success,
    string? ReleaseManifestPath,
    string? ArtifactDirectory,
    string Detail);

public interface IProjectReleaseBuildRunner
{
    Task<ProjectReleaseBuildResult> BuildAsync(
        string sourceRoot,
        string projectId,
        string version,
        string releaseId,
        string sourceRevision,
        string operationId,
        CancellationToken cancellationToken);
}

public sealed class ProjectReleaseBuildRunner(
    OpsPathResolver pathResolver,
    IOptions<OpsOptions> options) : IProjectReleaseBuildRunner
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();
    private readonly OpsOptions _options = options.Value;

    public async Task<ProjectReleaseBuildResult> BuildAsync(
        string sourceRoot,
        string projectId,
        string version,
        string releaseId,
        string sourceRevision,
        string operationId,
        CancellationToken cancellationToken)
    {
        var buildScript = Path.Combine(sourceRoot, "tools", "Build-CompanyOpsRelease.ps1");
        if (!File.Exists(buildScript))
        {
            return Failed($"项目缺少固定构建入口：{buildScript}");
        }

        var releaseToolRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var releaseBuilder = Path.Combine(releaseToolRoot, "tools", "New-ProjectRelease.ps1");
        if (!File.Exists(releaseBuilder))
        {
            return Failed("CompanyOps Agent 安装中缺少 tools\\New-ProjectRelease.ps1");
        }

        var outputRoot = Path.Combine(
            _paths.StateDirectory,
            "git-builds",
            SafeSegment(projectId),
            SafeSegment(operationId));
        if (Directory.Exists(outputRoot))
        {
            return Failed($"构建操作目录已存在，拒绝覆盖：{outputRoot}");
        }
        Directory.CreateDirectory(outputRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(_options.PowerShellExecutablePath),
            WorkingDirectory = sourceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", buildScript,
                     "-Version", version,
                     "-ReleaseId", releaseId,
                     "-OpsSpecificationRoot", releaseToolRoot,
                     "-OutputDirectory", outputRoot
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["COMPANYOPS_EXPECTED_SOURCE_REVISION"] = sourceRevision;

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return Failed("无法启动项目 Release 构建进程");
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.GitBuildTimeoutMinutes, 1, 120)));
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            var detail = Limit(string.Join(
                " | ",
                new[] { (await stdout).Trim(), (await stderr).Trim() }
                    .Where(static value => value.Length > 0)));
            if (process.ExitCode != 0)
            {
                return Failed($"项目 Release 构建失败（退出码 {process.ExitCode}）：{detail}");
            }

            var manifestPath = Path.Combine(outputRoot, "release-manifest.json");
            if (!File.Exists(manifestPath))
            {
                return Failed("项目构建成功返回，但没有生成 release-manifest.json");
            }
            return new ProjectReleaseBuildResult(true, manifestPath, outputRoot, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return Failed("项目 Release 构建超时");
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Failed(exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ProjectReleaseBuildResult Failed(string detail) =>
        new(false, null, null, detail);

    private static string SafeSegment(string value) =>
        Regex.Replace(value, "[^A-Za-z0-9._-]", "-");

    private static string ResolvePowerShell(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            var executableName = Path.GetFileName(fullPath);
            if (!File.Exists(fullPath) ||
                !(executableName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
                  executableName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Ops:PowerShellExecutablePath 必须指向存在的 pwsh.exe 或 powershell.exe");
            }
            return fullPath;
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe";
    }

    private static string Limit(string value) =>
        value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters];

    private static void TryTerminate(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Only the exact build process tree created for this operation is targeted.
        }
    }
}

public sealed class GitBuildReleaseService(
    IGitCommandRunner git,
    IGitCredentialStore credentials,
    IProjectReleaseBuildRunner builder,
    IDeploymentExecutor deploymentEngine,
    OperationGate operationGate,
    IOptions<OpsOptions> options) : IGitBuildReleaseService
{
    private static readonly TimeSpan LocalGitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkGitTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex VersionTag = new(
        "^v(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?)$",
        RegexOptions.CultureInvariant);
    private readonly OpsOptions _options = options.Value;

    public async Task<GitUpdateResult> ExecuteAsync(
        GitUpdateRequest request,
        ProjectRuntimeView project,
        JsonObject source,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                source["buildProfile"]?.GetValue<string>(),
                "projectReleaseV1",
                StringComparison.Ordinal))
        {
            return Reject(request, "build_profile_invalid", "gitBuildRelease 仅允许固定的 projectReleaseV1 构建能力");
        }

        var sourceRoot = ResolveRepositoryRoot(project.SourceRoot);
        if (sourceRoot is null)
        {
            return Reject(request, "source_repository_invalid", "EnvironmentBinding roots.source 不是可用的独立 Git 仓库");
        }
        if (!IsAllowedProjectRoot(sourceRoot))
        {
            return Reject(
                request,
                "source_root_not_allowed",
                "项目源码目录不在 Ops:AllowedProjectInstallRoots 的受控父目录内");
        }
        if (string.IsNullOrWhiteSpace(project.InstallRoot))
        {
            return Reject(request, "install_root_missing", "项目缺少独立安装目录绑定");
        }
        if (PathsOverlap(sourceRoot, project.InstallRoot))
        {
            return Reject(
                request,
                "source_install_roots_overlap",
                "源码目录与安装目录必须彼此独立，且不能互相包含");
        }

        var inspection = await InspectAsync(project, sourceRoot, source, cancellationToken);
        if (inspection.Error is not null)
        {
            return Reject(
                request,
                inspection.Error.Value.Code,
                inspection.Error.Value.Detail,
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                inspection.Steps);
        }

        var result = new GitUpdateResult(
            request.OperationId,
            request.Action,
            OperationOutcome.Succeeded,
            request.ProjectId,
            request.Environment,
            inspection.UpdateAvailable,
            inspection.UpdateAvailable,
            inspection.InstalledCommit,
            inspection.RemoteCommit,
            inspection.ChangedFiles,
            inspection.Steps,
            Detail: inspection.UpdateAvailable
                ? $"发现可构建发布的版本 {inspection.Version}，确认后在源码仓库构建并走受控制品更新。"
                : "当前安装 Release 已对应远端目标提交。")
        {
            Version = inspection.Version,
            ReleaseId = inspection.ReleaseId
        };
        if (request.Action == GitUpdateAction.Check || !inspection.UpdateAvailable)
        {
            return result;
        }

        if (!string.Equals(request.ExpectedCurrentCommit, inspection.InstalledCommit, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedRemoteCommit, inspection.RemoteCommit, StringComparison.Ordinal))
        {
            return Reject(
                request,
                "git_plan_changed",
                "安装提交或远端提交已变化，请重新检查",
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                inspection.Steps);
        }

        using var sourceLease = operationGate.TryAcquire([
            $"git-build-source:{sourceRoot.ToUpperInvariant()}"
        ]);
        if (sourceLease is null)
        {
            return Reject(
                request,
                "git_build_busy",
                "当前源码仓库已有构建操作",
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                inspection.Steps);
        }

        var steps = inspection.Steps.ToList();
        if (!string.Equals(inspection.RepositoryCommit, inspection.RemoteCommit, StringComparison.Ordinal))
        {
            var merge = await git.RunAsync(
                sourceRoot,
                ["merge", "--ff-only", $"refs/remotes/{inspection.Remote}/{inspection.Branch}"],
                LocalGitTimeout,
                cancellationToken);
            steps.Add($"源码仓库快进：{merge.Detail}");
            if (!merge.Success)
            {
                return Reject(
                    request,
                    "git_fast_forward_failed",
                    merge.Detail,
                    inspection.InstalledCommit,
                    inspection.RemoteCommit,
                    inspection.ChangedFiles,
                    steps);
            }
        }

        var build = await builder.BuildAsync(
            sourceRoot,
            request.ProjectId,
            inspection.Version!,
            inspection.ReleaseId!,
            inspection.RemoteCommit!,
            request.OperationId,
            cancellationToken);
        steps.Add(build.Success ? "固定项目 Release 构建完成" : build.Detail);
        if (!build.Success || build.ReleaseManifestPath is null || build.ArtifactDirectory is null)
        {
            return Reject(
                request,
                "release_build_failed",
                build.Detail,
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                steps);
        }

        var manifestError = await ValidateBuiltManifestAsync(
            build.ReleaseManifestPath,
            request.ProjectId,
            inspection.Version!,
            inspection.ReleaseId!,
            inspection.RemoteCommit!,
            cancellationToken);
        if (manifestError is not null)
        {
            return Reject(
                request,
                "built_manifest_mismatch",
                manifestError,
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                steps);
        }
        steps.Add("构建 Manifest 的项目、版本、ReleaseId 与 sourceRevision 已绑定目标提交");

        var deploymentAction = project.HasInstalledState
            ? DeploymentAction.Update
            : DeploymentAction.Install;
        var planOperationId = DerivedIdentifier(request.OperationId, "-plan", 100);
        var planIdempotencyKey = DerivedIdentifier(request.IdempotencyKey, "-plan", 200);
        var deployOperationId = DerivedIdentifier(request.OperationId, "-deploy", 100);
        var deployIdempotencyKey = DerivedIdentifier(request.IdempotencyKey, "-deploy", 200);
        var plan = await deploymentEngine.ExecuteAsync(
            new DeploymentRequest(
                planOperationId,
                planIdempotencyKey,
                request.ProjectId,
                request.Environment,
                DeploymentAction.Plan,
                request.ExpectedGeneration,
                build.ReleaseManifestPath,
                build.ArtifactDirectory),
            cancellationToken);
        steps.AddRange(plan.Steps.Select(static step => "部署计划：" + step));
        if (plan.Outcome != OperationOutcome.Succeeded)
        {
            return Reject(
                request,
                plan.ErrorCode ?? "deployment_plan_failed",
                plan.Detail ?? "构建制品未通过部署计划",
                inspection.InstalledCommit,
                inspection.RemoteCommit,
                inspection.ChangedFiles,
                steps);
        }

        var deployment = await deploymentEngine.ExecuteAsync(
            new DeploymentRequest(
                deployOperationId,
                deployIdempotencyKey,
                request.ProjectId,
                request.Environment,
                deploymentAction,
                request.ExpectedGeneration,
                build.ReleaseManifestPath,
                build.ArtifactDirectory),
            cancellationToken);
        steps.AddRange(deployment.Steps.Select(static step => "受控发布：" + step));
        return new GitUpdateResult(
            request.OperationId,
            request.Action,
            deployment.Outcome,
            request.ProjectId,
            request.Environment,
            deployment.Outcome != OperationOutcome.Succeeded,
            false,
            inspection.InstalledCommit,
            inspection.RemoteCommit,
            inspection.ChangedFiles,
            steps,
            deployment.ErrorCode,
            deployment.Outcome == OperationOutcome.Succeeded
                ? $"{inspection.Version} 已由 Git 目标提交构建并完成受控发布。"
                : deployment.Detail)
        {
            Version = inspection.Version,
            ReleaseId = inspection.ReleaseId
        };
    }

    private async Task<BuildInspection> InspectAsync(
        ProjectRuntimeView project,
        string repositoryRoot,
        JsonObject source,
        CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        var remote = source["remote"]?.GetValue<string>() ?? string.Empty;
        var branch = source["branch"]?.GetValue<string>() ?? string.Empty;
        var expectedRemoteUrl = source["remoteUrl"]?.GetValue<string>() ?? string.Empty;

        var status = await git.RunAsync(
            repositoryRoot,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            LocalGitTimeout,
            cancellationToken);
        if (!status.Success || !string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return BuildInspection.Fail(
                status.Success ? "working_tree_dirty" : "git_status_failed",
                status.Success ? "服务器项目源码仓库存在未提交或未跟踪文件" : status.Detail,
                steps);
        }
        steps.Add("独立源码仓库工作树干净");

        var currentBranch = await git.RunAsync(
            repositoryRoot, ["branch", "--show-current"], LocalGitTimeout, cancellationToken);
        if (!currentBranch.Success || currentBranch.StandardOutput.Trim() != branch)
        {
            return BuildInspection.Fail("branch_mismatch", $"当前源码分支不是声明的 {branch}", steps);
        }

        var actualRemote = await git.RunAsync(
            repositoryRoot, ["remote", "get-url", remote], LocalGitTimeout, cancellationToken);
        if (!actualRemote.Success || !UrlsEqual(actualRemote.StandardOutput.Trim(), expectedRemoteUrl))
        {
            return BuildInspection.Fail("remote_mismatch", $"源码仓库远端 {remote} 与项目声明不一致", steps);
        }
        steps.Add($"源码分支及远端归属通过：{remote}/{branch}");

        var current = await git.RunAsync(
            repositoryRoot, ["rev-parse", "HEAD"], LocalGitTimeout, cancellationToken);
        var repositoryCommit = current.StandardOutput.Trim();
        if (!current.Success || !ValidCommit(repositoryCommit))
        {
            return BuildInspection.Fail("current_commit_unavailable", current.Detail, steps);
        }

        var credential = credentials.Find(expectedRemoteUrl);
        var fetch = await git.RunAsync(
            repositoryRoot,
            [
                "fetch", "--prune", remote,
                $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}",
                "refs/tags/*:refs/tags/*"
            ],
            NetworkGitTimeout,
            cancellationToken,
            credential);
        if (!fetch.Success)
        {
            return BuildInspection.Fail(
                IsAuthenticationFailure(fetch.Detail)
                    ? credential is null ? "git_credentials_required" : "git_credential_rejected"
                    : "git_fetch_failed",
                IsAuthenticationFailure(fetch.Detail)
                    ? credential is null
                        ? "该 HTTPS Git 远端需要身份验证，请先配置仓库凭据"
                        : "已保存的 Git 仓库凭据被远端拒绝"
                    : fetch.Detail,
                steps);
        }
        steps.Add("远端分支和发布标签读取成功（未修改安装目录）");

        var remoteRef = $"refs/remotes/{remote}/{branch}";
        var remoteCommitResult = await git.RunAsync(
            repositoryRoot, ["rev-parse", remoteRef], LocalGitTimeout, cancellationToken);
        var remoteCommit = remoteCommitResult.StandardOutput.Trim();
        if (!remoteCommitResult.Success || !ValidCommit(remoteCommit))
        {
            return BuildInspection.Fail("remote_commit_unavailable", remoteCommitResult.Detail, steps);
        }

        if (!string.Equals(repositoryCommit, remoteCommit, StringComparison.Ordinal))
        {
            var ancestor = await git.RunAsync(
                repositoryRoot,
                ["merge-base", "--is-ancestor", repositoryCommit, remoteCommit],
                LocalGitTimeout,
                cancellationToken);
            if (!ancestor.Success)
            {
                return BuildInspection.Fail("non_fast_forward", "源码工作树与远端已经分叉", steps);
            }
            steps.Add("源码仓库可安全快进到远端目标提交");
        }

        var tag = await git.RunAsync(
            repositoryRoot,
            ["tag", "--points-at", remoteCommit, "--list", "v*"],
            LocalGitTimeout,
            cancellationToken);
        var versions = tag.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => VersionTag.Match(value))
            .Where(static match => match.Success)
            .Select(static match => match.Groups["version"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!tag.Success || versions.Length != 1)
        {
            return BuildInspection.Fail(
                "release_tag_required",
                "远端目标提交必须且只能有一个 v<major>.<minor>.<patch>[-prerelease] 发布标签",
                steps);
        }

        string? installedCommit;
        try
        {
            installedCommit = await ReadInstalledRevisionAsync(project.InstallRoot!, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return BuildInspection.Fail("installed_release_invalid", exception.Message, steps);
        }
        if (project.HasInstalledState && !ValidCommit(installedCommit))
        {
            return BuildInspection.Fail(
                "installed_source_revision_missing",
                "当前 InstalledState 对应的 ReleaseManifest 缺少有效 sourceRevision",
                steps);
        }

        var changedFiles = Array.Empty<string>();
        if (ValidCommit(installedCommit) && !string.Equals(installedCommit, remoteCommit, StringComparison.Ordinal))
        {
            var diff = await git.RunAsync(
                repositoryRoot,
                ["diff", "--name-only", "--diff-filter=ACDMRTUXB", $"{installedCommit}..{remoteCommit}"],
                LocalGitTimeout,
                cancellationToken);
            if (!diff.Success)
            {
                return BuildInspection.Fail("git_diff_failed", diff.Detail, steps);
            }
            changedFiles = diff.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToArray();
        }

        var version = versions[0];
        var releaseId = $"{project.ProjectId}-{version}-{remoteCommit[..12]}";
        steps.Add($"目标标签 v{version}，ReleaseId {releaseId}");
        return new BuildInspection(
            !string.Equals(installedCommit, remoteCommit, StringComparison.Ordinal),
            installedCommit,
            repositoryCommit,
            remoteCommit,
            remote,
            branch,
            version,
            releaseId,
            changedFiles,
            steps,
            null);
    }

    private static async Task<string?> ReadInstalledRevisionAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var pointerPath = Path.Combine(root, "current.release.json");
        if (!File.Exists(pointerPath))
        {
            return null;
        }
        var pointer = JsonNode.Parse(await File.ReadAllTextAsync(pointerPath, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("current.release.json 不是有效 JSON object");
        var currentPathText = pointer["currentPath"]?.GetValue<string>()
            ?? throw new InvalidDataException("current.release.json 缺少 currentPath");
        var currentPath = Path.GetFullPath(currentPathText);
        var releasesRoot = Path.Combine(root, "releases");
        var prefix = Path.TrimEndingDirectorySeparator(releasesRoot) + Path.DirectorySeparatorChar;
        if (!currentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("当前 Release 路径逃逸安装目录");
        }
        var releaseManifestPath = Path.Combine(currentPath, ".companyops", "release-manifest.json");
        if (!File.Exists(releaseManifestPath))
        {
            throw new InvalidDataException("当前 Release 缺少受管 ReleaseManifest");
        }
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(releaseManifestPath, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("当前 ReleaseManifest 无效");
        return manifest["metadata"]?["sourceRevision"]?.GetValue<string>();
    }

    private static async Task<string?> ValidateBuiltManifestAsync(
        string manifestPath,
        string projectId,
        string version,
        string releaseId,
        string sourceRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))?.AsObject();
            var metadata = root?["metadata"]?.AsObject();
            if (metadata?["projectId"]?.GetValue<string>() != projectId ||
                metadata["version"]?.GetValue<string>() != version ||
                metadata["releaseId"]?.GetValue<string>() != releaseId ||
                !string.Equals(
                    metadata["sourceRevision"]?.GetValue<string>(),
                    sourceRevision,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "构建 Manifest 与目标项目、版本、ReleaseId 或 Git 提交不一致";
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static GitUpdateResult Reject(
        GitUpdateRequest request,
        string errorCode,
        string detail,
        string? currentCommit = null,
        string? remoteCommit = null,
        IReadOnlyList<string>? changedFiles = null,
        IReadOnlyList<string>? steps = null) =>
        new(
            request.OperationId,
            request.Action,
            OperationOutcome.Rejected,
            request.ProjectId,
            request.Environment,
            remoteCommit is not null && remoteCommit != currentCommit,
            false,
            currentCommit,
            remoteCommit,
            changedFiles ?? [],
            steps ?? [],
            errorCode,
            detail);

    private static bool UrlsEqual(string actual, string expected) =>
        string.Equals(
            actual.Trim().TrimEnd('/'),
            expected.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool ValidCommit(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    private static string DerivedIdentifier(string value, string suffix, int maximumLength)
    {
        if (value.Length + suffix.Length <= maximumLength)
        {
            return value + suffix;
        }

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
        var prefixLength = maximumLength - suffix.Length - digest.Length - 1;
        return value[..prefixLength] + "-" + digest + suffix;
    }

    private static bool PathsOverlap(string first, string second)
    {
        try
        {
            var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
            var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var leftPrefix = left + Path.DirectorySeparatorChar;
            var rightPrefix = right + Path.DirectorySeparatorChar;
            return left.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static string? ResolveRepositoryRoot(string? rootPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
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

    private static bool IsAuthenticationFailure(string detail) =>
        detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("HTTP Basic: Access denied", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("askpass", StringComparison.OrdinalIgnoreCase);

    private bool IsAllowedProjectRoot(string projectRoot)
    {
        if (_options.AllowedProjectInstallRoots is not { Length: > 0 })
        {
            return false;
        }
        try
        {
            var resolved = Path.GetFullPath(projectRoot);
            return _options.AllowedProjectInstallRoots.Any(configuredRoot =>
            {
                if (string.IsNullOrWhiteSpace(configuredRoot))
                {
                    return false;
                }
                var allowed = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(configuredRoot.Trim()));
                var driveRoot = Path.GetPathRoot(allowed);
                if (driveRoot is null || string.Equals(
                        Path.TrimEndingDirectorySeparator(allowed),
                        Path.TrimEndingDirectorySeparator(driveRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var prefix = Path.TrimEndingDirectorySeparator(allowed) + Path.DirectorySeparatorChar;
                return resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record BuildInspection(
        bool UpdateAvailable,
        string? InstalledCommit,
        string? RepositoryCommit,
        string? RemoteCommit,
        string? Remote,
        string? Branch,
        string? Version,
        string? ReleaseId,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> Steps,
        (string Code, string Detail)? Error)
    {
        public static BuildInspection Fail(
            string code,
            string detail,
            IReadOnlyList<string> steps) =>
            new(false, null, null, null, null, null, null, null, [], steps, (code, detail));
    }
}
