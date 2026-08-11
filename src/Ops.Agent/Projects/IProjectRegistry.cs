using CompanyOps.Contracts;

namespace CompanyOps.Agent.Projects;

public interface IProjectRegistry
{
    Task<ProjectRegistrySnapshot> BuildAsync(
        ManifestCatalogSnapshot catalog,
        InventorySnapshot inventory,
        CancellationToken cancellationToken);
}
