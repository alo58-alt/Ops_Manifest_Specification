using System.Net;
using System.Net.Sockets;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Tests;

public sealed class DeclaredHealthGateTests
{
    [Fact]
    public async Task TcpProbe_UsesBoundPortAndPassesOnlyWhenListening()
    {
        using var directory = new TestDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var projectPath = Path.Combine(directory.FullPath, "project.json");
        var bindingPath = Path.Combine(directory.FullPath, "binding.json");
        File.WriteAllText(
            projectPath,
            """
            {
              "components": [
                { "id": "api", "health": [ { "kind": "tcp", "portRef": "api-http", "timeoutSeconds": 1 } ] }
              ]
            }
            """);
        File.WriteAllText(
            bindingPath,
            $$"""
            {
              "metadata": { "environment": "test" },
              "portBindings": [ { "portId": "api-http", "address": "127.0.0.1", "port": {{port}} } ]
            }
            """);
        var catalog = new ManifestCatalogSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ManifestCatalogEntry(projectPath, "ProjectManifest", "sample", true, DateTimeOffset.UtcNow, []),
                new ManifestCatalogEntry(bindingPath, "EnvironmentBinding", "sample", true, DateTimeOffset.UtcNow, [])
            ]);
        var cache = new AgentSnapshotCache();
        cache.Update(new InventorySnapshot("TEST", DateTimeOffset.UtcNow, []), catalog);

        var result = await new DeclaredHealthGate(cache).ProbeAsync(
            "sample",
            "test",
            "api",
            CancellationToken.None);

        Assert.True(result.Success, result.Detail);
    }
}
