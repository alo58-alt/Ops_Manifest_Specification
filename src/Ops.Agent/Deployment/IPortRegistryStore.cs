using CompanyOps.Contracts;

namespace CompanyOps.Agent.Deployment;

public interface IPortRegistryStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<PortReservationResult> ReserveAsync(
        IReadOnlyList<PortReservationRequest> requests,
        CancellationToken cancellationToken);

    Task ReleaseOperationAsync(string operationId, CancellationToken cancellationToken);

    Task CommitOperationAsync(string operationId, CancellationToken cancellationToken);
}
