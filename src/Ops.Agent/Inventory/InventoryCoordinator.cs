using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class InventoryCoordinator(
    OpsPathResolver pathResolver,
    IEnumerable<IInventorySource> sources,
    ILogger<InventoryCoordinator> logger)
{
    private readonly IReadOnlyList<IInventorySource> _sources = sources.ToArray();
    private readonly string _hostId = pathResolver.Resolve().HostId;

    public async Task<InventorySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var tasks = _sources.Select(source => CollectSourceAsync(source, cancellationToken));
        var sections = await Task.WhenAll(tasks);
        return new InventorySnapshot(
            _hostId,
            DateTimeOffset.UtcNow,
            sections.OrderBy(static section => section.Source, StringComparer.Ordinal).ToArray());
    }

    private async Task<InventorySection> CollectSourceAsync(
        IInventorySource source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.CollectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "只读盘点源 {SourceName} 失败", source.Name);
            return new InventorySection(
                source.Name,
                InventorySourceStatus.Failed,
                [],
                exception.Message);
        }
    }
}
