using System.Text.Json;
using CompanyOps.Agent.Inventory;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class Pm2OwnershipEvaluatorTests
{
    [Fact]
    public async Task ExactNameCwdAndScript_IsMatched()
    {
        using var testDirectory = new TestDirectory();
        var snapshotDirectory = Path.Combine(testDirectory.FullPath, "snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        var expectedCwd = Path.Combine(testDirectory.FullPath, "legacy");
        var expectedScript = Path.Combine(expectedCwd, "worker.py");
        var claim = CreateClaim(expectedCwd, expectedScript);
        await WriteSnapshotAsync(
            snapshotDirectory,
            claim,
            [
                new Pm2ProcessSnapshot(
                    claim.ProcessName,
                    7,
                    expectedCwd,
                    expectedScript,
                    "online",
                    1234,
                    2)
            ]);

        var reader = CreateReader(testDirectory.FullPath, snapshotDirectory);
        var snapshotResult = await reader.ReadAsync(claim, CancellationToken.None);
        var result = Pm2OwnershipEvaluator.Evaluate(claim, snapshotResult);

        Assert.Equal(Pm2OwnershipState.Matched, result.State);
        Assert.Equal(7, result.Process?.PmId);
    }

    [Fact]
    public void SameNameWithDifferentPath_IsConflict()
    {
        using var testDirectory = new TestDirectory();
        var claim = CreateClaim(
            Path.Combine(testDirectory.FullPath, "legacy"),
            Path.Combine(testDirectory.FullPath, "legacy", "worker.py"));
        var snapshot = new Pm2Snapshot(
            "ops-pm2-snapshot/v1",
            claim.OwnerSid!,
            DateTimeOffset.UtcNow,
            10,
            [
                new Pm2ProcessSnapshot(
                    claim.ProcessName,
                    7,
                    Path.Combine(testDirectory.FullPath, "other"),
                    Path.Combine(testDirectory.FullPath, "other", "worker.py"),
                    "online",
                    1234,
                    0)
            ]);

        var result = Pm2OwnershipEvaluator.Evaluate(
            claim,
            new Pm2SnapshotReadResult(
                snapshot,
                Pm2OwnershipState.Matched,
                "ok"));

        Assert.Equal(Pm2OwnershipState.Conflict, result.State);
    }

    [Fact]
    public void DuplicateExactName_IsConflict()
    {
        using var testDirectory = new TestDirectory();
        var expectedCwd = Path.Combine(testDirectory.FullPath, "legacy");
        var expectedScript = Path.Combine(expectedCwd, "worker.py");
        var claim = CreateClaim(expectedCwd, expectedScript);
        var process = new Pm2ProcessSnapshot(
            claim.ProcessName,
            7,
            expectedCwd,
            expectedScript,
            "online",
            1234,
            0);
        var snapshot = new Pm2Snapshot(
            "ops-pm2-snapshot/v1",
            claim.OwnerSid!,
            DateTimeOffset.UtcNow,
            10,
            [process, process with { PmId = 8, Pid = 1235 }]);

        var result = Pm2OwnershipEvaluator.Evaluate(
            claim,
            new Pm2SnapshotReadResult(
                snapshot,
                Pm2OwnershipState.Matched,
                "ok"));

        Assert.Equal(Pm2OwnershipState.Conflict, result.State);
        Assert.Contains("2 个同名", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotOlderThanBindingLimit_IsStale()
    {
        using var testDirectory = new TestDirectory();
        var snapshotDirectory = Path.Combine(testDirectory.FullPath, "snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        var claim = CreateClaim(
            Path.Combine(testDirectory.FullPath, "legacy"),
            Path.Combine(testDirectory.FullPath, "legacy", "worker.py"));
        await WriteSnapshotAsync(
            snapshotDirectory,
            claim,
            [],
            DateTimeOffset.UtcNow.AddMinutes(-2));

        var reader = CreateReader(testDirectory.FullPath, snapshotDirectory);
        var result = await reader.ReadAsync(claim, CancellationToken.None);

        Assert.Equal(Pm2OwnershipState.SnapshotStale, result.State);
    }

    private static LegacyPm2Claim CreateClaim(string expectedCwd, string expectedScript) =>
        new(
            "sample-system",
            "production",
            "TEST-HOST",
            "legacy-worker",
            "遗留 Worker",
            "sample-system-legacy-worker",
            expectedCwd,
            expectedScript,
            "S-1-5-21-1000000000-2000000000-3000000000-1001",
            "sample-system.pm2.json",
            "CompanyOps.Pm2Bridge.Test.v1",
            30,
            null);

    private static Pm2SnapshotReader CreateReader(
        string stateDirectory,
        string snapshotDirectory)
    {
        var options = Options.Create(
            new OpsOptions
            {
                HostId = "TEST-HOST",
                ManifestDirectory = Path.Combine(stateDirectory, "manifests"),
                StateDirectory = stateDirectory,
                Pm2SnapshotDirectory = snapshotDirectory
            });
        return new Pm2SnapshotReader(
            new OpsPathResolver(options),
            AgentProtocol.CreateJsonSerializerOptions());
    }

    private static async Task WriteSnapshotAsync(
        string snapshotDirectory,
        LegacyPm2Claim claim,
        IReadOnlyList<Pm2ProcessSnapshot> processes,
        DateTimeOffset? capturedAt = null)
    {
        var snapshot = new Pm2Snapshot(
            "ops-pm2-snapshot/v1",
            claim.OwnerSid!,
            capturedAt ?? DateTimeOffset.UtcNow,
            10,
            processes);
        var json = JsonSerializer.Serialize(
            snapshot,
            AgentProtocol.CreateJsonSerializerOptions());
        await File.WriteAllTextAsync(
            Path.Combine(snapshotDirectory, claim.SnapshotFileName!),
            json);
    }
}
