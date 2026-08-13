using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CompanyOps.Agent.Tests;

public sealed class SqliteOpsStateStoreTests
{
    [Fact]
    public async Task InitializeAndAudit_RoundTripsWithoutBusinessState()
    {
        using var testDirectory = new TestDirectory();
        var store = CreateStore(testDirectory.FullPath);
        var auditEvent = new AuditEvent(
            Guid.CreateVersion7().ToString(),
            DateTimeOffset.UtcNow,
            "test",
            "read-only",
            "succeeded",
            "测试事件",
            JsonSerializer.SerializeToElement(new { operationId = "test-operation", steps = new[] { "预检", "完成" } }));

        await store.InitializeAsync(CancellationToken.None);
        await store.AppendAuditEventAsync(auditEvent, CancellationToken.None);
        await store.SaveInventorySnapshotAsync(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [
                    new InventorySection(
                        "test-source",
                        InventorySourceStatus.Available,
                        [])
                ]),
            CancellationToken.None);

        var events = await store.ReadRecentAuditEventsAsync(10, CancellationToken.None);

        var actual = Assert.Single(events);
        Assert.Equal(auditEvent.EventId, actual.EventId);
        Assert.Equal("read-only", actual.Action);
        Assert.Equal("test-operation", actual.Data?.GetProperty("operationId").GetString());
        Assert.True(File.Exists(Path.Combine(testDirectory.FullPath, "ops-agent.db")));
    }

    private static SqliteOpsStateStore CreateStore(string stateDirectory)
    {
        var options = Options.Create(
            new OpsOptions
            {
                HostId = "TEST-HOST",
                ManifestDirectory = Path.Combine(stateDirectory, "manifests"),
                StateDirectory = stateDirectory
            });
        return new SqliteOpsStateStore(
            new OpsPathResolver(options),
            TestDirectory.CreateJsonOptions());
    }
}
