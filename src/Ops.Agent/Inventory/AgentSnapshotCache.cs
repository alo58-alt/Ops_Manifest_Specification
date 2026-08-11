using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class AgentSnapshotCache
{
    private readonly object _sync = new();
    private InventorySnapshot? _inventory;
    private ManifestCatalogSnapshot? _catalog;
    private ProjectRegistrySnapshot? _projects;

    public void Update(
        InventorySnapshot inventory,
        ManifestCatalogSnapshot catalog,
        ProjectRegistrySnapshot? projects = null)
    {
        lock (_sync)
        {
            _inventory = inventory;
            _catalog = catalog;
            _projects = projects;
        }
    }

    public (InventorySnapshot? Inventory, ManifestCatalogSnapshot? Catalog, ProjectRegistrySnapshot? Projects) Read()
    {
        lock (_sync)
        {
            return (_inventory, _catalog, _projects);
        }
    }
}
