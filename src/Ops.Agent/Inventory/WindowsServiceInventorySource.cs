using System.ServiceProcess;
using CompanyOps.Agent.Deployment;
using CompanyOps.Contracts;
using Microsoft.Win32;

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

                try
                {
                    metadata["binaryPath"] = WindowsServiceConfiguration.QueryBinaryPath(service.ServiceName);
                    ReadNssmParameters(service.ServiceName, metadata);
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    metadata["binaryPath"] = null;
                    metadata["configurationError"] = exception.Message;
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

    private static void ReadNssmParameters(
        string serviceName,
        IDictionary<string, string?> metadata)
    {
        using var parameters = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}\Parameters",
            writable: false);
        if (parameters is null)
        {
            return;
        }

        metadata["nssmApplication"] = parameters.GetValue("Application") as string;
        metadata["nssmAppDirectory"] = parameters.GetValue("AppDirectory") as string;
        metadata["nssmAppParameters"] = parameters.GetValue("AppParameters") as string;
    }
}
