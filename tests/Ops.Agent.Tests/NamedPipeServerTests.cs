using System.IO.Pipes;
using System.Text.Json;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Pipe;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Deployment;
using CompanyOps.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class NamedPipeServerTests
{
    [Fact]
    public async Task Ping_ReturnsReadOnlyAgentIdentity()
    {
        using var testDirectory = new TestDirectory();
        var pipeName = $"CompanyOps.Agent.Tests.{Guid.CreateVersion7()}";
        var options = Options.Create(
            new OpsOptions
            {
                HostId = "TEST-HOST",
                ManifestDirectory = Path.Combine(testDirectory.FullPath, "manifests"),
                StateDirectory = testDirectory.FullPath,
                PipeName = pipeName,
                InventoryIntervalSeconds = 30
            });
        var pathResolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var store = new SqliteOpsStateStore(pathResolver, jsonOptions);
        await store.InitializeAsync(CancellationToken.None);
        var portStore = new SqlitePortRegistryStore(pathResolver);
        await portStore.InitializeAsync(CancellationToken.None);

        var cache = new AgentSnapshotCache();
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        var server = new NamedPipeServer(
            new NamedPipeSecurityFactory(options),
            cache,
            store,
            pathResolver,
            jsonOptions,
            new OperationCoordinator(
                cache,
                store,
                new OperationGate(),
                [],
                new AlwaysHealthyGate(),
                options,
                jsonOptions),
            new DeploymentEngine(
                cache,
                new ArtifactPackageValidator(pathResolver),
                new SafeZipExtractor(),
                portStore,
                new PassthroughDeploymentActivator(),
                new OperationGate(),
                store,
                pathResolver,
                options,
                jsonOptions),
            options,
            NullLogger<NamedPipeServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            var request = new AgentRequest(
                AgentProtocol.Version,
                "ping",
                Guid.CreateVersion7().ToString());
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions);
            await client.WriteAsync(requestBytes, timeout.Token);
            await client.WriteAsync("\n"u8.ToArray(), timeout.Token);
            await client.FlushAsync(timeout.Token);

            using var reader = new StreamReader(client, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync(timeout.Token);

            Assert.NotNull(responseLine);
            using var response = JsonDocument.Parse(responseLine);
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            var data = response.RootElement.GetProperty("data");
            Assert.Equal("TEST-HOST", data.GetProperty("hostId").GetString());
            Assert.Equal("read-only", data.GetProperty("mode").GetString());
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }
}
