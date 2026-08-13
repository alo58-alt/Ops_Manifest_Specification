using CompanyOps.Contracts;

namespace CompanyOps.Agent.Operations;

public sealed record ComponentControlTarget(
    string ProjectId,
    string Environment,
    string ComponentId,
    string Kind,
    string NativeId,
    string? InstallRoot = null,
    int? PmId = null);

public sealed record AdapterExecutionResult(bool Success, string? Detail = null);

public interface IComponentControlAdapter
{
    string Kind { get; }

    Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken);
}
