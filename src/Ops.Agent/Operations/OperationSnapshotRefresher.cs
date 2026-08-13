using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Projects;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Operations;

public interface IOperationSnapshotRefresher
{
    Task RefreshAsync(CancellationToken cancellationToken);
}

public sealed class OperationSnapshotRefresher(
    IManifestCatalog manifestCatalog,
    InventoryCoordinator inventoryCoordinator,
    IProjectRegistry projectRegistry,
    IComponentHealthGate healthGate,
    AgentSnapshotCache snapshotCache) : IOperationSnapshotRefresher
{
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var catalogTask = manifestCatalog.InspectAsync(cancellationToken);
        var inventoryTask = inventoryCoordinator.CollectAsync(cancellationToken);
        await Task.WhenAll(catalogTask, inventoryTask);
        var catalog = await catalogTask;
        var inventory = await inventoryTask;
        var projects = await projectRegistry.BuildAsync(catalog, inventory, cancellationToken);
        snapshotCache.Update(inventory, catalog, projects);

        var refreshedProjects = new List<ProjectRuntimeView>();
        foreach (var project in projects.Projects)
        {
            var components = new List<ProjectComponentRuntimeView>();
            foreach (var component in project.Components)
            {
                if (component.Ownership != ComponentOwnershipStatus.Owned)
                {
                    components.Add(component);
                    continue;
                }

                var health = await healthGate.ProbeAsync(
                    project.ProjectId,
                    project.Environment,
                    component.ComponentId,
                    cancellationToken);
                components.Add(component with
                {
                    HealthState = health.Success ? "healthy" : "unhealthy",
                    Detail = health.Detail ?? component.Detail
                });
            }

            refreshedProjects.Add(project with { Components = components });
        }

        snapshotCache.Update(
            inventory,
            catalog,
            projects with { Projects = refreshedProjects });
    }
}
