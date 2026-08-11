using CompanyOps.Contracts;

namespace CompanyOps.Agent.Persistence;

public interface IOpsStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveInventorySnapshotAsync(
        InventorySnapshot snapshot,
        CancellationToken cancellationToken);

    Task AppendAuditEventAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvent>> ReadRecentAuditEventsAsync(
        int limit,
        CancellationToken cancellationToken);
}
