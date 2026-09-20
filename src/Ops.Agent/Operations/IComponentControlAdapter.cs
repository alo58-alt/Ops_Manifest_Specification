using CompanyOps.Contracts;

namespace CompanyOps.Agent.Operations;

public sealed record ComponentControlTarget(
    string ProjectId,
    string Environment,
    string ComponentId,
    string Kind,
    string NativeId,
    string? InstallRoot = null,
    int? PmId = null,
    string? ExpectedCwd = null,
    string? ExpectedScript = null,
    IReadOnlyList<string>? ExpectedArguments = null,
    string? ControlPipeName = null);

public sealed record AdapterExecutionResult(bool Success, string? Detail = null, int? PmId = null);

public interface IComponentControlAdapter
{
    string Kind { get; }

    Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken);
}
