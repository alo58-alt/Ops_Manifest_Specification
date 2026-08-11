using System.ServiceProcess;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class WindowsServiceInventorySource : IInventorySource
{
    public string Name => "windows-services";

    public Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(
                new InventorySection(
                    Name,
                    InventorySourceStatus.Unavailable,
                    [],
                    "仅支持 Windows"));
        }

        var items = new List<InventoryItem>();
        foreach (var service in ServiceController
                     .GetServices()
                     .OrderBy(static service => service.ServiceName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (service)
            {
                var metadata = new Dictionary<string, string?>
                {
                    ["serviceName"] = service.ServiceName,
                    ["serviceType"] = service.ServiceType.ToString()
                };

                try
                {
                    metadata["startType"] = service.StartType.ToString();
                }
                catch (InvalidOperationException)
                {
                    metadata["startType"] = null;
                }

                items.Add(
                    new InventoryItem(
                        service.ServiceName,
                        service.DisplayName,
                        service.Status.ToString(),
                        metadata));
            }
        }

        return Task.FromResult(
            new InventorySection(Name, InventorySourceStatus.Available, items));
    }
}
