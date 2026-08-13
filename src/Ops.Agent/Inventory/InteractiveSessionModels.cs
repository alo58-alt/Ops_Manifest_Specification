namespace CompanyOps.Agent.Inventory;

public sealed record InteractiveSessionClaim(
    string ProjectId,
    string Environment,
    string HostId,
    string ComponentId,
    string DisplayName,
    string? ExpectedExecutable,
    string? ExpectedWorkingDirectory,
    IReadOnlyList<string> ExpectedArguments,
    string? OwnerSid,
    string? SnapshotFileName,
    string? ControlPipeName,
    int MaxAgeSeconds,
    string? BindingError);

public sealed record InteractiveSnapshotReadResult(
    CompanyOps.Contracts.InteractiveAppSnapshot? Snapshot,
    string State,
    string Detail);

public interface IInteractiveSessionClaimProvider
{
    Task<IReadOnlyList<InteractiveSessionClaim>> GetClaimsAsync(CancellationToken cancellationToken);
}
