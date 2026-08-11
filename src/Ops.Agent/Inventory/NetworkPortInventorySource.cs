using System.Net.NetworkInformation;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class NetworkPortInventorySource : IInventorySource
{
    public string Name => "network-listeners";

    public Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var items = new List<InventoryItem>();

        foreach (var endpoint in properties
                     .GetActiveTcpListeners()
                     .OrderBy(static endpoint => endpoint.Port)
                     .ThenBy(static endpoint => endpoint.Address.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(CreateItem("tcp", endpoint.Address.ToString(), endpoint.Port));
        }

        foreach (var endpoint in properties
                     .GetActiveUdpListeners()
                     .OrderBy(static endpoint => endpoint.Port)
                     .ThenBy(static endpoint => endpoint.Address.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(CreateItem("udp", endpoint.Address.ToString(), endpoint.Port));
        }

        return Task.FromResult(
            new InventorySection(Name, InventorySourceStatus.Available, items));
    }

    private static InventoryItem CreateItem(string protocol, string address, int port) =>
        new(
            $"{protocol}:{address}:{port}",
            $"{protocol.ToUpperInvariant()} {address}:{port}",
            "listening",
            new Dictionary<string, string?>
            {
                ["protocol"] = protocol,
                ["address"] = address,
                ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ownership"] = "unresolved"
            });
}
