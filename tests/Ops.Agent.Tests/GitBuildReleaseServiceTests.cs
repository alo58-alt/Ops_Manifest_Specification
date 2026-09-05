using System.Collections.Concurrent;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Updates;
using CompanyOps.Contracts;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class GitBuildReleaseServiceTests
{
    private const string CurrentCommit = "1111111111111111111111111111111111111111";
    private const string RemoteCommit = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task Check_SeparateSourceRepository_ReportsTaggedBuildRelease()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var runner = new FakeGitRunner();
        var builder = new RecordingBuilder(directory.FullPath);
        var deployments = new RecordingDeploymentExecutor();
        var service = CreateService(directory.FullPath, runner, builder, deployments);

        var result = await service.ExecuteAsync(
            Request(GitUpdateAction.Check),
            Project(directory.FullPath),
            Source(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.CanApply);
        Assert.Equal("3.0.4", result.Version);
        Assert.Equal("webquizbot-3.0.4-222222222222", result.ReleaseId);
        Assert.Empty(builder.Calls);
        Assert.Empty(deployments.Calls);
        Assert.Contains(runner.Calls, call => call.StartsWith("fetch --prune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_BuildsTaggedCommitThenUsesDeploymentEngine()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var runner = new FakeGitRunner();
        var builder = new RecordingBuilder(directory.FullPath);
        var deployments = new RecordingDeploymentExecutor();
        var service = CreateService(directory.FullPath, runner, builder, deployments);

        var result = await service.ExecuteAsync(
            Request(GitUpdateAction.Apply),
            Project(directory.FullPath),
            Source(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Single(builder.Calls);
        Assert.Equal(RemoteCommit, builder.Calls.Single().SourceRevision);
        Assert.Equal(
            [DeploymentAction.Plan, DeploymentAction.Install],
            deployments.Calls.Select(call => call.Action).ToArray());
        Assert.Contains(runner.Calls, call => call.StartsWith("merge --ff-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_RemoteCommitWithoutSingleVersionTag_Rejects()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var runner = new FakeGitRunner { TagOutput = string.Empty };
        var service = CreateService(
            directory.FullPath,
            runner,
            new RecordingBuilder(directory.FullPath),
            new RecordingDeploymentExecutor());

        var result = await service.ExecuteAsync(
            Request(GitUpdateAction.Check),
            Project(directory.FullPath),
            Source(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("release_tag_required", result.ErrorCode);
    }

    [Fact]
    public async Task Check_SourceAndInstallRootsOverlap_RejectsBeforeGitAccess()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var runner = new FakeGitRunner();
        var project = Project(directory.FullPath) with
        {
            InstallRoot = Path.Combine(directory.FullPath, "install")
        };
        var service = CreateService(
            directory.FullPath,
            runner,
            new RecordingBuilder(directory.FullPath),
            new RecordingDeploymentExecutor());

        var result = await service.ExecuteAsync(
            Request(GitUpdateAction.Check),
            project,
            Source(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("source_install_roots_overlap", result.ErrorCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Apply_MaximumLengthIdentifiers_DerivesValidDeploymentIdentifiers()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var deployments = new RecordingDeploymentExecutor();
        var service = CreateService(
            directory.FullPath,
            new FakeGitRunner(),
            new RecordingBuilder(directory.FullPath),
            deployments);
        var request = Request(GitUpdateAction.Apply) with
        {
            OperationId = new string('a', 100),
            IdempotencyKey = new string('b', 200)
        };

        var result = await service.ExecuteAsync(
            request,
            Project(directory.FullPath),
            Source(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.All(deployments.Calls, call => Assert.True(call.OperationId.Length <= 100));
        Assert.All(deployments.Calls, call => Assert.True(call.IdempotencyKey.Length <= 200));
        Assert.Equal(2, deployments.Calls.Select(call => call.OperationId).Distinct().Count());
    }

    private static GitBuildReleaseService CreateService(
        string projectRoot,
        IGitCommandRunner runner,
        IProjectReleaseBuildRunner builder,
        IDeploymentExecutor deployments) =>
        new(
            runner,
            new EmptyCredentialStore(),
            builder,
            deployments,
            new OperationGate(),
            Options.Create(new OpsOptions
            {
                AllowedProjectInstallRoots = [Path.GetDirectoryName(projectRoot)!]
            }));

    private static GitUpdateRequest Request(GitUpdateAction action) =>
        new(
            $"build-{action}",
            $"build-{action}-{Guid.CreateVersion7()}",
            "webquizbot",
            "production",
            action,
            1,
            null,
            action == GitUpdateAction.Apply ? RemoteCommit : null);

    private static ProjectRuntimeView Project(string root) =>
        new(
            "webquizbot",
            "WebQuizBot",
            "production",
            ProjectBindingStatus.Declared,
            null,
            1,
            [new ProjectComponentRuntimeView(
                "api", "API", "windowsService", "WebQuizBot", "WebQuizBot",
                ComponentOwnershipStatus.Owned, "running", "healthy", null)],
            [])
        {
            SourceRoot = root,
            InstallRoot = Path.Combine(
                Path.GetDirectoryName(root)!,
                Path.GetFileName(root) + "-install"),
            GitUpdateEnabled = true,
            GitUpdateKind = "gitBuildRelease",
            HasInstalledState = false
        };

    private static JsonObject Source() =>
        JsonNode.Parse(
            """
            {
              "kind": "gitBuildRelease",
              "remote": "origin",
              "branch": "master",
              "remoteUrl": "https://gitee.com/xu-zong2/webquizbot.git",
              "buildProfile": "projectReleaseV1"
            }
            """)!.AsObject();

    private sealed class FakeGitRunner : IGitCommandRunner
    {
        public string TagOutput { get; init; } = "v3.0.4\n";
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<GitCommandResult> RunAsync(
            string repositoryRoot,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            GitCredentialHandle? credential = null)
        {
            var command = string.Join(' ', arguments);
            Calls.Enqueue(command);
            var output = arguments[0] switch
            {
                "status" => string.Empty,
                "branch" => "master\n",
                "remote" => "https://gitee.com/xu-zong2/webquizbot.git\n",
                "rev-parse" when arguments[1] == "HEAD" => CurrentCommit + "\n",
                "rev-parse" => RemoteCommit + "\n",
                "tag" => TagOutput,
                "diff" => "server.py\n",
                _ => string.Empty
            };
            return Task.FromResult(new GitCommandResult(true, 0, output, string.Empty));
        }
    }

    private sealed class RecordingBuilder(string root) : IProjectReleaseBuildRunner
    {
        public ConcurrentQueue<BuildCall> Calls { get; } = new();

        public async Task<ProjectReleaseBuildResult> BuildAsync(
            string sourceRoot,
            string projectId,
            string version,
            string releaseId,
            string sourceRevision,
            string operationId,
            CancellationToken cancellationToken)
        {
            Calls.Enqueue(new BuildCall(version, releaseId, sourceRevision));
            var output = Path.Combine(root, "artifact", operationId);
            Directory.CreateDirectory(output);
            var manifest = Path.Combine(output, "release-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                $$"""
                {
                  "metadata": {
                    "projectId": "{{projectId}}",
                    "version": "{{version}}",
                    "releaseId": "{{releaseId}}",
                    "sourceRevision": "{{sourceRevision}}"
                  }
                }
                """,
                cancellationToken);
            return new ProjectReleaseBuildResult(true, manifest, output, "ok");
        }
    }

    private sealed class RecordingDeploymentExecutor : IDeploymentExecutor
    {
        public ConcurrentQueue<DeploymentRequest> Calls { get; } = new();

        public Task<DeploymentResult> ExecuteAsync(
            DeploymentRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Enqueue(request);
            return Task.FromResult(new DeploymentResult(
                request.OperationId,
                request.Action,
                OperationOutcome.Succeeded,
                request.ProjectId,
                request.Environment,
                null,
                "3.0.4",
                ["ok"],
                Detail: "ok"));
        }
    }

    private sealed class EmptyCredentialStore : IGitCredentialStore
    {
        public GitCredentialHandle? Find(string remoteUrl) => null;

        public GitCredentialHandle Save(string remoteUrl, string username, string secret) =>
            throw new NotSupportedException();
    }

    private sealed record BuildCall(string Version, string ReleaseId, string SourceRevision);
}
