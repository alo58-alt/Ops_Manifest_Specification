using System.Collections.Concurrent;
using CompanyOps.Agent;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Updates;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class GitUpdateServiceTests
{
    private const string CurrentCommit = "1111111111111111111111111111111111111111";
    private const string RemoteCommit = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task Check_CleanFastForwardWithoutDependencyChange_CanApply()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("playwright_controller.py\nops/project-manifest.json\n");
        var service = await CreateServiceAsync(directory, runner);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Check), CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.UpdateAvailable);
        Assert.True(result.CanApply);
        Assert.Equal(CurrentCommit, result.CurrentCommit);
        Assert.Equal(RemoteCommit, result.RemoteCommit);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("reset", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("clean", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_DependencyManifestChanged_RequiresReleaseArtifact()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("requirements.txt\nserver.py\n");
        var service = await CreateServiceAsync(directory, runner);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Check), CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.UpdateAvailable);
        Assert.False(result.CanApply);
        Assert.Contains(result.Steps, step => step.Contains("requirements.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_DirtyWorkingTree_RejectsBeforeFetchOrServiceControl()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        runner.StatusOutput = " M server.py\n";
        var adapter = new RecordingWindowsServiceAdapter();
        var service = await CreateServiceAsync(directory, runner, adapter);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Check), CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("working_tree_dirty", result.ErrorCode);
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("fetch", StringComparison.Ordinal));
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Check_PrivateRemoteWithoutCredential_ReturnsActionableError()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        runner.FetchFailure = "fatal: could not read Username for 'https://gitee.com': terminal prompts disabled";
        var service = await CreateServiceAsync(directory, runner);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Check), CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("git_credentials_required", result.ErrorCode);
        Assert.Contains("配置仓库凭据", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_SavedCredential_IsUsedOnlyForFetch()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        var service = await CreateServiceAsync(
            directory,
            runner,
            credentials: new FixedCredentialStore());

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Check), CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Single(runner.CredentialCalls);
        Assert.StartsWith("fetch ", runner.CredentialCalls.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ChangedPlan_IsRejectedBeforeStoppingService()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        var adapter = new RecordingWindowsServiceAdapter();
        var service = await CreateServiceAsync(directory, runner, adapter);
        var request = Request(GitUpdateAction.Apply) with
        {
            ExpectedCurrentCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ExpectedRemoteCommit = RemoteCommit
        };

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("git_plan_changed", result.ErrorCode);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Apply_FastForwardsThenRestartsAndChecksHealth()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        var adapter = new RecordingWindowsServiceAdapter();
        var service = await CreateServiceAsync(directory, runner, adapter);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Apply), CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            [ComponentOperationAction.Stop, ComponentOperationAction.Start],
            adapter.Calls);
        Assert.Contains(runner.Calls, call => call.StartsWith("merge --ff-only", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("clean", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("--hard", StringComparison.Ordinal));

        var audit = Assert.Single(await ReadGitAuditAsync(directory));
        Assert.NotNull(audit.Data);
        Assert.Equal(CurrentCommit, audit.Data.Value.GetProperty("fromCommit").GetString());
        Assert.Equal(RemoteCommit, audit.Data.Value.GetProperty("toCommit").GetString());
        Assert.Contains(
            audit.Data.Value.GetProperty("steps").EnumerateArray(),
            step => step.GetString()?.StartsWith("Git 快进：", StringComparison.Ordinal) == true);
        Assert.False(audit.Data.Value.GetProperty("rolledBack").GetBoolean());
    }

    [Fact]
    public async Task Apply_StartFailure_RollsBackWithKeepAndRestartsOriginalService()
    {
        using var directory = new TestDirectory();
        var runner = SuccessfulRunner("server.py\n");
        var adapter = new RecordingWindowsServiceAdapter { FailFirstStart = true };
        var service = await CreateServiceAsync(directory, runner, adapter);

        var result = await service.ExecuteAsync(Request(GitUpdateAction.Apply), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal("service_start_failed", result.ErrorCode);
        Assert.Contains(runner.Calls, call => call == $"reset --keep {CurrentCommit}");
        Assert.DoesNotContain(runner.Calls, call => call.Contains("--hard", StringComparison.Ordinal));
        Assert.Equal(ComponentOperationAction.Start, adapter.Calls.Last());

        var audit = Assert.Single(await ReadGitAuditAsync(directory));
        Assert.True(audit.Data?.GetProperty("rolledBack").GetBoolean());
    }

    private static GitUpdateRequest Request(GitUpdateAction action) =>
        new(
            $"git-{action}",
            $"git-{action}-{Guid.CreateVersion7()}",
            "webquizbot",
            "production",
            action,
            1,
            action == GitUpdateAction.Apply ? CurrentCommit : null,
            action == GitUpdateAction.Apply ? RemoteCommit : null);

    private static FakeGitCommandRunner SuccessfulRunner(string changedFiles) =>
        new()
        {
            CurrentCommit = CurrentCommit,
            RemoteCommit = RemoteCommit,
            ChangedFiles = changedFiles
        };

    private static async Task<IReadOnlyList<AuditEvent>> ReadGitAuditAsync(TestDirectory directory)
    {
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = directory.FullPath,
            StateDirectory = directory.FullPath
        });
        var store = new SqliteOpsStateStore(
            new OpsPathResolver(options),
            TestDirectory.CreateJsonOptions());
        return (await store.ReadRecentAuditEventsAsync(20, CancellationToken.None))
            .Where(item => item.Category == "git-update")
            .ToArray();
    }

    private static async Task<GitUpdateService> CreateServiceAsync(
        TestDirectory directory,
        FakeGitCommandRunner runner,
        RecordingWindowsServiceAdapter? adapter = null,
        IGitCredentialStore? credentials = null)
    {
        Directory.CreateDirectory(Path.Combine(directory.FullPath, ".git"));
        var manifestPath = Path.Combine(directory.FullPath, "webquizbot.project-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "metadata": { "id": "webquizbot" },
              "update": {
                "rollbackOnFailure": true,
                "healthTimeoutSeconds": 60,
                "source": {
                  "kind": "gitFastForward",
                  "remote": "origin",
                  "branch": "master",
                  "remoteUrl": "https://gitee.com/xu-zong2/webquizbot.git"
                }
              }
            }
            """);
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = directory.FullPath,
            StateDirectory = directory.FullPath,
            EnableExistingGitUpdates = true
        });
        var resolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var store = new SqliteOpsStateStore(resolver, jsonOptions);
        await store.InitializeAsync(CancellationToken.None);
        var project = new ProjectRuntimeView(
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
            InstallRoot = directory.FullPath,
            GitUpdateEnabled = true
        };
        var cache = new AgentSnapshotCache();
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            new ManifestCatalogSnapshot(
                DateTimeOffset.UtcNow,
                [new ManifestCatalogEntry(
                    manifestPath, "ProjectManifest", "webquizbot", true,
                    DateTimeOffset.UtcNow, [])]),
            new ProjectRegistrySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [project]));
        adapter ??= new RecordingWindowsServiceAdapter();
        return new GitUpdateService(
            cache,
            store,
            new OperationGate(),
            new NoopSnapshotRefresher(),
            new AlwaysHealthyGate(),
            [adapter],
            runner,
            credentials ?? new GitCredentialStore(resolver),
            options,
            jsonOptions);
    }

    private sealed class NoopSnapshotRefresher : IOperationSnapshotRefresher
    {
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingWindowsServiceAdapter : IComponentControlAdapter
    {
        public string Kind => "windowsService";
        public ConcurrentQueue<ComponentOperationAction> Calls { get; } = new();
        public bool FailFirstStart { get; init; }
        private int _startCalls;

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken)
        {
            Calls.Enqueue(action);
            if (action == ComponentOperationAction.Start &&
                FailFirstStart &&
                Interlocked.Increment(ref _startCalls) == 1)
            {
                return Task.FromResult(new AdapterExecutionResult(false, "simulated start failure"));
            }
            return Task.FromResult(new AdapterExecutionResult(true, "ok"));
        }
    }

    private sealed class FakeGitCommandRunner : IGitCommandRunner
    {
        public string StatusOutput { get; set; } = string.Empty;
        public string CurrentCommit { get; init; } = string.Empty;
        public string RemoteCommit { get; init; } = string.Empty;
        public string ChangedFiles { get; init; } = string.Empty;
        public string? FetchFailure { get; set; }
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> CredentialCalls { get; } = new();

        public Task<GitCommandResult> RunAsync(
            string repositoryRoot,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            GitCredentialHandle? credential = null)
        {
            var command = string.Join(' ', arguments);
            Calls.Enqueue(command);
            if (credential is not null)
            {
                CredentialCalls.Enqueue(command);
            }
            if (arguments[0] == "fetch" && FetchFailure is not null)
            {
                return Task.FromResult(new GitCommandResult(false, 128, string.Empty, FetchFailure));
            }
            var output = arguments[0] switch
            {
                "status" => StatusOutput,
                "branch" => "master\n",
                "remote" => "https://gitee.com/xu-zong2/webquizbot.git\n",
                "rev-parse" when arguments[1] == "HEAD" => CurrentCommit + "\n",
                "rev-parse" => RemoteCommit + "\n",
                "diff" => ChangedFiles,
                _ => string.Empty
            };
            return Task.FromResult(new GitCommandResult(true, 0, output, string.Empty));
        }
    }

    private sealed class FixedCredentialStore : IGitCredentialStore
    {
        public GitCredentialHandle? Find(string remoteUrl) => new("C:\\CompanyOps.Tests\\credential.bin");

        public GitCredentialHandle Save(string remoteUrl, string username, string secret) =>
            Find(remoteUrl)!;
    }
}
