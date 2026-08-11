using System.Text.Json;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Tests;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void TransportRequest_IsExactlyOneJsonLine()
    {
        var request = new AgentRequest(
            AgentProtocol.Version,
            "ping",
            Guid.CreateVersion7().ToString());

        var json = JsonSerializer.Serialize(
            request,
            AgentProtocol.CreateJsonSerializerOptions());

        Assert.DoesNotContain('\r', json);
        Assert.DoesNotContain('\n', json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("ping", parsed.RootElement.GetProperty("command").GetString());
    }
}
