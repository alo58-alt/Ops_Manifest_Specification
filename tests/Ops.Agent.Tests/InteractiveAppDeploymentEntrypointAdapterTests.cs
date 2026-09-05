using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Inventory;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class InteractiveAppDeploymentEntrypointAdapterTests
{
    [Fact]
    public async Task Capture_AllowsFirstInstallWhenDeclaredExeDoesNotExist()
    {
        using var directory = new TestDirectory();
        var workingDirectory = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(workingDirectory);
        var executable = Path.Combine(workingDirectory, "Future.Host.exe");
        var fixture = CreateAdapter(directory.FullPath, executable, workingDirectory);
        var target = new DeploymentEntrypointTarget(
            "sample",
            "production",
            "host",
            "interactiveApp",
            "host",
            Path.Combine(directory.FullPath, "release", "Future.Host.exe"),
            "unused",
            Path.Combine(directory.FullPath, "release"),
            []);

        var result = await fixture.Adapter.CaptureAsync(target, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        Assert.NotNull(result.Snapshot);
        Assert.False(result.Snapshot.WasRunning);
        Assert.False(result.Snapshot.HadManagedState);
        Assert.Contains("首次 Install", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_RejectsMissingExeWhenManagedEntrypointAlreadyExists()
    {
        using var directory = new TestDirectory();
        var workingDirectory = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(workingDirectory);
        var executable = Path.Combine(workingDirectory, "Missing.Host.exe");
        var fixture = CreateAdapter(directory.FullPath, executable, workingDirectory);
        await fixture.Entrypoints.WriteAsync(
            new InteractiveEntrypointState(
                InteractiveSessionProtocol.EntrypointStateVersion,
                "sample",
                "production",
                "host",
                executable,
                workingDirectory,
                [],
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var target = new DeploymentEntrypointTarget(
            "sample",
            "production",
            "host",
            "interactiveApp",
            "host",
            Path.Combine(directory.FullPath, "release", "Host.exe"),
            "unused",
            Path.Combine(directory.FullPath, "release"),
            []);

        var result = await fixture.Adapter.CaptureAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("已登记", result.Detail, StringComparison.Ordinal);
    }

    private static AdapterFixture CreateAdapter(
        string root,
        string executable,
        string workingDirectory)
    {
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = Path.Combine(root, "manifests"),
            StateDirectory = Path.Combine(root, "state"),
            InteractiveSnapshotDirectory = Path.Combine(root, "snapshots")
        });
        var resolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var entrypoints = new InteractiveEntrypointStateStore(resolver, jsonOptions);
        var claims = new FixedClaims(
        [
            new InteractiveSessionClaim(
                "sample",
                "production",
                "TEST-HOST",
                "host",
                "Future Host",
                executable,
                workingDirectory,
                [],
                "S-1-5-21-1234",
                "sample.snapshot.json",
                "CompanyOps.SessionAgent.test",
                30,
                null)
        ]);
        return new AdapterFixture(
            new InteractiveAppDeploymentEntrypointAdapter(
                entrypoints,
                claims,
                new InteractiveSnapshotReader(resolver, jsonOptions)),
            entrypoints);
    }

    private sealed class FixedClaims(IReadOnlyList<InteractiveSessionClaim> claims)
        : IInteractiveSessionClaimProvider
    {
        public Task<IReadOnlyList<InteractiveSessionClaim>> GetClaimsAsync(
            CancellationToken cancellationToken) => Task.FromResult(claims);
    }

    private sealed record AdapterFixture(
        InteractiveAppDeploymentEntrypointAdapter Adapter,
        InteractiveEntrypointStateStore Entrypoints);
}
