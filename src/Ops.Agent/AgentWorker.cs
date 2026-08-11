using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Projects;
using CompanyOps.Agent.Deployment;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent;

public sealed class AgentWorker(
    IManifestCatalog manifestCatalog,
    InventoryCoordinator inventoryCoordinator,
    AgentSnapshotCache snapshotCache,
    IOpsStateStore stateStore,
    IProjectRegistry projectRegistry,
    IPortRegistryStore portRegistry,
    IOptions<OpsOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await stateStore.InitializeAsync(stoppingToken);
        await portRegistry.InitializeAsync(stoppingToken);
        await stateStore.AppendAuditEventAsync(
            new AuditEvent(
                Guid.CreateVersion7().ToString(),
                DateTimeOffset.UtcNow,
                "agent",
                "startup",
                "succeeded",
                options.Value.EnableMutations
                    ? "Agent 已启动；变更能力已由主机配置显式启用。"
                    : "Agent 已启动；变更能力保持默认关闭。"),
            stoppingToken);

        var intervalSeconds = Math.Clamp(options.Value.InventoryIntervalSeconds, 5, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            try
            {
                var catalogTask = manifestCatalog.InspectAsync(stoppingToken);
                var inventoryTask = inventoryCoordinator.CollectAsync(stoppingToken);
                await Task.WhenAll(catalogTask, inventoryTask);

                var catalog = await catalogTask;
                var inventory = await inventoryTask;
                var projects = await projectRegistry.BuildAsync(catalog, inventory, stoppingToken);
                snapshotCache.Update(inventory, catalog, projects);
                await stateStore.SaveInventorySnapshotAsync(inventory, stoppingToken);

                logger.LogInformation(
                    "只读盘点完成：{SectionCount} 个来源，{ManifestCount} 个 Manifest，{ProjectCount} 个项目环境",
                    inventory.Sections.Count,
                    catalog.Entries.Count,
                    projects.Projects.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "只读盘点轮次失败");
                await TryRecordFailureAsync(exception, stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TryRecordFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.AppendAuditEventAsync(
                new AuditEvent(
                    Guid.CreateVersion7().ToString(),
                    DateTimeOffset.UtcNow,
                    "inventory",
                    "collect",
                    "failed",
                    exception.Message),
                cancellationToken);
        }
        catch (Exception auditException)
        {
            logger.LogError(auditException, "写入盘点失败审计事件时再次失败");
        }
    }
}
