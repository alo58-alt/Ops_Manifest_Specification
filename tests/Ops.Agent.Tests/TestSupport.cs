using System.Text.Json;
using CompanyOps.Contracts;
using CompanyOps.Agent.Operations;
using Microsoft.Data.Sqlite;

namespace CompanyOps.Agent.Tests;

internal sealed class TestDirectory : IDisposable
{
    private static readonly string TestRoot = Path.Combine(
        Path.GetTempPath(),
        "CompanyOps.Agent.Tests");

    public TestDirectory()
    {
        FullPath = Path.Combine(TestRoot, Guid.CreateVersion7().ToString());
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(FullPath);
        var resolvedRoot = Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理测试根目录之外的路径");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    public static JsonSerializerOptions CreateJsonOptions() =>
        AgentProtocol.CreateJsonSerializerOptions();
}

internal sealed class AlwaysHealthyGate : IComponentHealthGate
{
    public Task<HealthGateResult> ProbeAsync(
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HealthGateResult(true, "test"));
}
