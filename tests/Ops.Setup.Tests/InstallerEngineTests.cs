using Xunit;

namespace CompanyOps.Setup.Tests;

public sealed class InstallerEngineTests
{
    [Fact]
    public void TryParseServiceProcessId_ReadsQueryExPid()
    {
        const string output = """
            SERVICE_NAME: CompanyOps.Agent
                    TYPE               : 10  WIN32_OWN_PROCESS
                    STATE              : 4  RUNNING
                    PID                : 17840
            """;

        Assert.Equal(17840, InstallerEngine.TryParseServiceProcessId(output));
    }

    [Fact]
    public void TryParseServiceProcessId_ReturnsNullWhenPidIsMissing()
    {
        Assert.Null(InstallerEngine.TryParseServiceProcessId("STATE : 1 STOPPED"));
    }

    [Fact]
    public void MoveDirectoryWithRetry_MovesProductComponent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CompanyOps.Setup.Tests.{Guid.NewGuid():N}");
        var source = Path.Combine(root, "Agent");
        var destination = Path.Combine(root, "Agent.backup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "marker.txt"), "original");

        try
        {
            InstallerEngine.MoveDirectoryWithRetry(
                source,
                destination,
                "Agent",
                TimeSpan.FromSeconds(1));

            Assert.False(Directory.Exists(source));
            Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ProductComponents_IncludeSessionAgentForInstallAndUpgrade()
    {
        var field = typeof(InstallerEngine).GetField(
            "ProductComponents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var components = Assert.IsType<string[]>(field?.GetValue(null));
        Assert.Contains("SessionAgent", components, StringComparer.Ordinal);
    }
}
