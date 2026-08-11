using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public interface IInventorySource
{
    string Name { get; }

    Task<InventorySection> CollectAsync(CancellationToken cancellationToken);
}
