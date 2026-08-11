using CompanyOps.Contracts;

namespace CompanyOps.Agent.Catalog;

public interface IManifestCatalog
{
    Task<ManifestCatalogSnapshot> InspectAsync(CancellationToken cancellationToken);
}
