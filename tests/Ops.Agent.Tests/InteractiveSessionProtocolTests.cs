using CompanyOps.Contracts;
using Xunit;

namespace CompanyOps.Agent.Tests;

public sealed class InteractiveSessionProtocolTests
{
    [Fact]
    public void OwnerSpecificNames_AreStableAndDoNotExposeSid()
    {
        const string sid = "S-1-5-21-100-200-300-400";

        var pipe = InteractiveSessionProtocol.PipeName(sid);
        var snapshot = InteractiveSessionProtocol.SnapshotFileName("sample-ui", "production", sid);
        var entrypoint = InteractiveSessionProtocol.EntrypointStateFileName(
            "sample-ui",
            "production",
            "desktop");

        Assert.Equal(pipe, InteractiveSessionProtocol.PipeName(sid));
        Assert.StartsWith("CompanyOps.SessionAgent.", pipe, StringComparison.Ordinal);
        Assert.DoesNotContain(sid, pipe, StringComparison.Ordinal);
        Assert.EndsWith(".json", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(sid, snapshot, StringComparison.Ordinal);
        Assert.Equal(entrypoint, InteractiveSessionProtocol.EntrypointStateFileName(
            "sample-ui",
            "production",
            "desktop"));
        Assert.EndsWith(".json", entrypoint, StringComparison.Ordinal);
    }
}
