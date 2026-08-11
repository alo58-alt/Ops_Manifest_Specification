using System.Collections.Concurrent;
using CompanyOps.Agent;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public void Gate_RejectsOverlappingResources_AndReleasesAll()
    {
        var gate = new OperationGate();
        using var first = gate.TryAcquire(["service:a", "port:1000"]);
        Assert.NotNull(first);
        Assert.Null(gate.TryAcquire(["service:b", "port:1000"]));

        first.Dispose();
        using var second = gate.TryAcquire(["service:b", "port:1000"]);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task MutationsDisabled_RejectsBeforeAdapterExecution()
    {
        using var directory = new TestDirectory();
        var calls = new ConcurrentQueue<string>();
        var coordinator = await CreateCoordinatorAsync(directory, calls, enableMutations: false);

        var result = await coordinator.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("mutations_disabled", result.ErrorCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Start_UsesDependencyOrder_AndSameIdempotencyReturnsCachedResult()
    {
        using var directory = new TestDirectory();
        var calls = new ConcurrentQueue<string>();
        var coordinator = await CreateCoordinatorAsync(directory, calls, enableMutations: true);

        var first = await coordinator.ExecuteAsync(Request(), CancellationToken.None);
        var second = await coordinator.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, first.Outcome);
        Assert.Equal(first, second);
        Assert.Equal(["api:Start", "web:Start"], calls);
    }

    [Fact]
    public async Task SameIdempotencyWithDifferentRequest_IsRejected()
    {
        using var directory = new TestDirectory();
        var calls = new ConcurrentQueue<string>();
        var coordinator = await CreateCoordinatorAsync(directory, calls, enableMutations: true);
        await coordinator.ExecuteAsync(Request(), CancellationToken.None);

        var result = await coordinator.ExecuteAsync(
            Request() with { Action = ComponentOperationAction.Stop },
            CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("idempotency_conflict", result.ErrorCode);
    }

    private static ComponentOperationRequest Request() =>
        new(
            "op-1",
            "idem-1",
            "sample",
            "test",
            "web",
            ComponentOperationAction.Start,
            1);

    private static async Task<OperationCoordinator> CreateCoordinatorAsync(
        TestDirectory directory,
        ConcurrentQueue<string> calls,
        bool enableMutations)
    {
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = directory.FullPath,
            StateDirectory = directory.FullPath,
            PipeName = "test",
            InventoryIntervalSeconds = 30,
            EnableMutations = enableMutations
        });
        var resolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var store = new SqliteOpsStateStore(resolver, jsonOptions);
        await store.InitializeAsync(CancellationToken.None);
        var manifestPath = Path.Combine(directory.FullPath, "project.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "components": [
                { "id": "api", "dependsOn": [] },
                { "id": "web", "dependsOn": ["api"] }
              ]
            }
            """);
        var catalog = new ManifestCatalogSnapshot(
            DateTimeOffset.UtcNow,
            [new ManifestCatalogEntry(
                manifestPath,
                "ProjectManifest",
                "sample",
                true,
                DateTimeOffset.UtcNow,
                [])]);
        var project = new ProjectRuntimeView(
            "sample",
            "Sample",
            "test",
            ProjectBindingStatus.Installed,
            "1.0.0",
            1,
            [
                new ProjectComponentRuntimeView(
                    "api", "API", "windowsService", "Company.Sample.Api", "Company.Sample.Api",
                    ComponentOwnershipStatus.Owned, "running", "healthy", null),
                new ProjectComponentRuntimeView(
                    "web", "Web", "staticSite", "Company.Sample.Web", "Company.Sample.Web",
                    ComponentOwnershipStatus.Owned, "running", "healthy", null)
            ],
            []);
        var cache = new AgentSnapshotCache();
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            catalog,
            new ProjectRegistrySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [project]));

        return new OperationCoordinator(
            cache,
            store,
            new OperationGate(),
            [new RecordingAdapter("windowsService", calls), new RecordingAdapter("staticSite", calls)],
            new AlwaysHealthyGate(),
            options,
            jsonOptions);
    }

    private sealed class RecordingAdapter(
        string kind,
        ConcurrentQueue<string> calls) : IComponentControlAdapter
    {
        public string Kind => kind;

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken)
        {
            calls.Enqueue($"{target.ComponentId}:{action}");
            return Task.FromResult(new AdapterExecutionResult(true));
        }
    }
}
