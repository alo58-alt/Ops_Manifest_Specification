using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

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

    [Fact]
    public async Task FileHeartbeat_UsesDeclaredBindingRootAndRejectsEscapes()
    {
        using var directory = new TestDirectory();
        var logsRoot = Path.Combine(directory.FullPath, "logs");
        Directory.CreateDirectory(logsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(logsRoot, "browser-host.json"),
            "{}",
            TestContext.Current.CancellationToken);
        var project = JsonNode.Parse(
            """
            {
              "components": [{
                "id": "host",
                "health": [{
                  "kind": "fileHeartbeat",
                  "rootRef": "logs",
                  "path": "browser-host.json",
                  "maxAgeSeconds": 30
                }]
              }]
            }
            """)!.AsObject();
        var binding = JsonNode.Parse(
            $$"""
            {
              "roots": {
                "install": {{JsonValue.Create(Path.Combine(directory.FullPath, "install"))!.ToJsonString()}},
                "data": {{JsonValue.Create(Path.Combine(directory.FullPath, "data"))!.ToJsonString()}},
                "logs": {{JsonValue.Create(logsRoot)!.ToJsonString()}}
              }
            }
            """)!.AsObject();
        var gate = new DeclaredHealthGate(new AgentSnapshotCache());

        var result = await gate.ProbeAsync(
            project,
            binding,
            "host",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        project["components"]![0]!["health"]![0]!["path"] = "../outside.json";
        result = await gate.ProbeAsync(
            project,
            binding,
            "host",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("路径逃逸", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveProcess_RequiresFreshExactSessionSnapshot()
    {
        using var directory = new TestDirectory();
        var snapshotDirectory = Path.Combine(directory.FullPath, "snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        const string snapshotFileName = "sample-production-owner.json";
        var executable = Path.Combine(directory.FullPath, "releases", "r1", "Host.exe");
        var workingDirectory = Path.GetDirectoryName(executable)!;
        var arguments = new[] { "--logs-dir", Path.Combine(directory.FullPath, "logs") };
        var claim = new InteractiveSessionClaim(
            "sample", "production", "TEST", "host", "Host",
            executable, workingDirectory, arguments,
            "S-1-5-21-1", snapshotFileName, "pipe", 30, null);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var snapshot = new InteractiveAppSnapshot(
            InteractiveSessionProtocol.SnapshotVersion,
            "S-1-5-21-1",
            1,
            DateTimeOffset.UtcNow,
            [new InteractiveAppProcessSnapshot(
                "sample", "production", "host", executable, workingDirectory,
                arguments, "running", 4321, DateTimeOffset.UtcNow)]);
        await File.WriteAllTextAsync(
            Path.Combine(snapshotDirectory, snapshotFileName),
            System.Text.Json.JsonSerializer.Serialize(snapshot, jsonOptions),
            TestContext.Current.CancellationToken);
        var resolver = new OpsPathResolver(Options.Create(new OpsOptions
        {
            HostId = "TEST",
            StateDirectory = directory.FullPath,
            InteractiveSnapshotDirectory = snapshotDirectory
        }));
        var gate = new DeclaredHealthGate(
            new AgentSnapshotCache(),
            new StaticClaimProvider([claim]),
            new InteractiveSnapshotReader(resolver, jsonOptions));
        var project = JsonNode.Parse(
            """
            {
              "metadata": { "id": "sample" },
              "components": [{ "id": "host", "health": [{ "kind": "interactiveProcess" }] }]
            }
            """)!.AsObject();
        var binding = JsonNode.Parse(
            """
            { "metadata": { "environment": "production" } }
            """)!.AsObject();

        var result = await gate.ProbeAsync(
            project, binding, "host", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        snapshot = snapshot with
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Processes = [snapshot.Processes[0] with { State = "stopped", ProcessId = null }]
        };
        await File.WriteAllTextAsync(
            Path.Combine(snapshotDirectory, snapshotFileName),
            System.Text.Json.JsonSerializer.Serialize(snapshot, jsonOptions),
            TestContext.Current.CancellationToken);
        result = await gate.ProbeAsync(
            project, binding, "host", TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("未运行", result.Detail, StringComparison.Ordinal);
    }

    private sealed class StaticClaimProvider(IReadOnlyList<InteractiveSessionClaim> claims)
        : IInteractiveSessionClaimProvider
    {
        public Task<IReadOnlyList<InteractiveSessionClaim>> GetClaimsAsync(
            CancellationToken cancellationToken) => Task.FromResult(claims);
    }
}
