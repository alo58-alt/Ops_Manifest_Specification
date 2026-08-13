using CompanyOps.Agent.Inventory;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class InteractiveEntrypointStateStoreTests
{
    [Fact]
    public async Task State_RoundTripsAtomicallyAndCanBeRemoved()
    {
        using var directory = new TestDirectory();
        var manifestDirectory = Path.Combine(directory.FullPath, "manifests");
        var executable = Path.Combine(directory.FullPath, "releases", "r1", "host.exe");
        var workingDirectory = Path.GetDirectoryName(executable)!;
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(executable, "fixture", TestContext.Current.CancellationToken);
        var store = CreateStore(directory.FullPath, manifestDirectory);
        var state = new InteractiveEntrypointState(
            InteractiveSessionProtocol.EntrypointStateVersion,
            "sample",
            "production",
            "host",
            executable,
            workingDirectory,
            ["--data-dir", Path.Combine(directory.FullPath, "data")],
            DateTimeOffset.UtcNow);

        await store.WriteAsync(state, TestContext.Current.CancellationToken);
        var read = await store.ReadAsync(
            "sample", "production", "host", TestContext.Current.CancellationToken);

        Assert.True(read.Exists);
        Assert.Null(read.Error);
        Assert.Equal(state.Executable, read.State!.Executable);
        Assert.Equal(state.Arguments, read.State.Arguments);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(manifestDirectory, "*.tmp", SearchOption.AllDirectories),
            static _ => true);

        await store.DeleteAsync(
            "sample", "production", "host", TestContext.Current.CancellationToken);
        read = await store.ReadAsync(
            "sample", "production", "host", TestContext.Current.CancellationToken);
        Assert.False(read.Exists);
    }

    [Fact]
    public async Task State_FailsClosedForCorruptOrMismatchedContent()
    {
        using var directory = new TestDirectory();
        var manifestDirectory = Path.Combine(directory.FullPath, "manifests");
        var store = CreateStore(directory.FullPath, manifestDirectory);
        var stateDirectory = Path.Combine(
            manifestDirectory,
            InteractiveSessionProtocol.EntrypointStateDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(
            stateDirectory,
            InteractiveSessionProtocol.EntrypointStateFileName("sample", "production", "host"));
        await File.WriteAllTextAsync(
            path,
            """
            {"protocolVersion":"wrong","projectId":"sample","environment":"production","componentId":"host","executable":"C:\\host.exe","workingDirectory":"C:\\","arguments":[],"updatedAt":"2026-08-13T00:00:00Z"}
            """,
            TestContext.Current.CancellationToken);

        var read = await store.ReadAsync(
            "sample", "production", "host", TestContext.Current.CancellationToken);

        Assert.True(read.Exists);
        Assert.Null(read.State);
        Assert.Contains("无效", read.Error, StringComparison.Ordinal);
    }

    private static InteractiveEntrypointStateStore CreateStore(string stateDirectory, string manifestDirectory) =>
        new(
            new OpsPathResolver(Options.Create(new OpsOptions
            {
                HostId = "TEST",
                ManifestDirectory = manifestDirectory,
                StateDirectory = stateDirectory
            })),
            TestDirectory.CreateJsonOptions());
}
