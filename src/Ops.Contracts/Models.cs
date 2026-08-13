using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanyOps.Contracts;

public static class AgentProtocol
{
    public const string Version = "ops-agent/v1";
    public const int MaximumRequestBytes = 65_536;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static JsonSerializerOptions CreateJsonSerializerOptions(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record AgentRequest(
    string ProtocolVersion,
    string Command,
    string? CorrelationId = null,
    JsonElement? Data = null);

public sealed record AgentResponse(
    string ProtocolVersion,
    string Command,
    bool Success,
    string CorrelationId,
    DateTimeOffset RespondedAt,
    JsonElement? Data = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record ManifestCatalogSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<ManifestCatalogEntry> Entries);

public sealed record ManifestCatalogEntry(
    string Path,
    string? ManifestKind,
    string? ProjectId,
    bool IsValid,
    DateTimeOffset LastWriteTimeUtc,
    IReadOnlyList<string> Errors);

public sealed record InventorySnapshot(
    string HostId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<InventorySection> Sections);

public sealed record InventorySection(
    string Source,
    InventorySourceStatus Status,
    IReadOnlyList<InventoryItem> Items,
    string? Detail = null);

public enum InventorySourceStatus
{
    Available,
    Unavailable,
    Failed
}

public sealed record InventoryItem(
    string Id,
    string DisplayName,
    string State,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record ProjectRegistrySnapshot(
    string HostId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ProjectRuntimeView> Projects);

public sealed record ProjectRuntimeView(
    string ProjectId,
    string DisplayName,
    string Environment,
    ProjectBindingStatus Status,
    string? InstalledVersion,
    long? Generation,
    IReadOnlyList<ProjectComponentRuntimeView> Components,
    IReadOnlyList<string> Problems)
{
    public string? InstallRoot { get; init; }

    public bool GitUpdateEnabled { get; init; }

    public bool HasInstalledState { get; init; }
}

public sealed record ProjectComponentRuntimeView(
    string ComponentId,
    string DisplayName,
    string Kind,
    string ExpectedNativeId,
    string? InstalledNativeId,
    ComponentOwnershipStatus Ownership,
    string RuntimeState,
    string HealthState,
    string? Detail);

public enum ProjectBindingStatus
{
    Declared,
    Installed,
    Degraded,
    Conflict
}

public enum ComponentOwnershipStatus
{
    DeclaredOnly,
    Owned,
    Missing,
    Unknown,
    Conflict
}

public sealed record ComponentOperationRequest(
    string OperationId,
    string IdempotencyKey,
    string ProjectId,
    string Environment,
    string ComponentId,
    ComponentOperationAction Action,
    long ExpectedGeneration);

public enum ComponentOperationAction
{
    Start,
    Stop,
    Restart
}

public sealed record ComponentOperationResult(
    string OperationId,
    string ProjectId,
    string Environment,
    string ComponentId,
    ComponentOperationAction Action,
    OperationOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ComponentOperationStep> Steps,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record ComponentOperationStep(
    string ComponentId,
    string Adapter,
    string Action,
    string Outcome,
    string? Detail = null);

public enum OperationOutcome
{
    Succeeded,
    Rejected,
    Failed
}

public sealed record GitUpdateRequest(
    string OperationId,
    string IdempotencyKey,
    string ProjectId,
    string Environment,
    GitUpdateAction Action,
    long ExpectedGeneration,
    string? ExpectedCurrentCommit = null,
    string? ExpectedRemoteCommit = null);

public enum GitUpdateAction
{
    Check,
    Apply
}

public sealed record GitUpdateResult(
    string OperationId,
    GitUpdateAction Action,
    OperationOutcome Outcome,
    string ProjectId,
    string Environment,
    bool UpdateAvailable,
    bool CanApply,
    string? CurrentCommit,
    string? RemoteCommit,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Steps,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record GitCredentialSetRequest(
    string OperationId,
    string IdempotencyKey,
    string ProjectId,
    string Environment,
    long ExpectedGeneration,
    string Username,
    string Secret);

public sealed record GitCredentialSetResult(
    string OperationId,
    OperationOutcome Outcome,
    string ProjectId,
    string Environment,
    string? RemoteHost,
    bool Configured,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record ArtifactValidationResult(
    bool Success,
    string ProjectId,
    string Version,
    IReadOnlyList<ValidatedArtifact> Artifacts,
    IReadOnlyList<string> Errors);

public sealed record ValidatedArtifact(
    string Id,
    string FileName,
    string FullPath,
    long SizeBytes,
    string Sha256);

public sealed record PortReservationRequest(
    string Protocol,
    string Address,
    int Port,
    string ProjectId,
    string Environment,
    string ComponentId,
    string PortId,
    string OperationId);

public sealed record PortReservationResult(
    bool Success,
    IReadOnlyList<PortReservationRequest> Reservations,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record DeploymentRequest(
    string OperationId,
    string IdempotencyKey,
    string ProjectId,
    string Environment,
    DeploymentAction Action,
    long ExpectedGeneration,
    string? ReleaseManifestPath = null,
    string? ArtifactDirectory = null);

public enum DeploymentAction
{
    Plan,
    Install,
    Update,
    Rollback
}

public sealed record DeploymentResult(
    string OperationId,
    DeploymentAction Action,
    OperationOutcome Outcome,
    string ProjectId,
    string Environment,
    string? FromVersion,
    string? ToVersion,
    IReadOnlyList<string> Steps,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record ExistingProjectOnboardingRequest(
    string ProjectRoot,
    string Environment,
    ExistingProjectOnboardingAction Action,
    string? ExpectedPlanToken = null,
    IReadOnlyDictionary<string, string>? NativeNames = null,
    IReadOnlyDictionary<string, int>? Ports = null,
    string? DataRoot = null,
    string? LogsRoot = null,
    string? InteractiveOwnerSid = null);

public enum ExistingProjectOnboardingAction
{
    Plan,
    Apply
}

public sealed record DirectoryBrowseRequest(string? Path = null);

public sealed record DirectoryBrowseResult(
    string? CurrentPath,
    string? ParentPath,
    bool IsProjectRoot,
    IReadOnlyList<DirectoryBrowseEntry> Directories);

public sealed record DirectoryBrowseEntry(string Name, string FullPath);

public sealed record ExistingProjectOnboardingResult(
    ExistingProjectOnboardingAction Action,
    OperationOutcome Outcome,
    string? ProjectId,
    string? DisplayName,
    string Environment,
    string HostId,
    bool CanApply,
    bool AlreadyOnboarded,
    string? PlanToken,
    IReadOnlyList<OnboardingComponentProposal> Components,
    IReadOnlyList<OnboardingPortProposal> Ports,
    IReadOnlyList<OnboardingHealthResult> Health,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Problems,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record OnboardingComponentProposal(
    string ComponentId,
    string DisplayName,
    string Kind,
    string? NativeName,
    bool RequiresInput,
    IReadOnlyList<string> Candidates);

public sealed record OnboardingPortProposal(
    string PortId,
    string ComponentId,
    string Protocol,
    string Address,
    int? Port,
    bool RequiresInput);

public sealed record OnboardingHealthResult(
    string ComponentId,
    bool Success,
    string? Detail);

public static class Pm2BridgeProtocol
{
    public const string Version = "ops-pm2-control/v1";
}

public sealed record Pm2BridgeControlRequest(
    string ProtocolVersion,
    string RequestId,
    int PmId,
    string Name,
    string ExpectedCwd,
    string ExpectedScript,
    ComponentOperationAction Action);

public sealed record Pm2BridgeControlResponse(
    string ProtocolVersion,
    string RequestId,
    bool Success,
    string? ErrorCode = null,
    string? Detail = null);

public static class InteractiveSessionProtocol
{
    public const string ControlVersion = "ops-interactive-control/v1";
    public const string SnapshotVersion = "ops-interactive-snapshot/v1";
    public const string EntrypointStateVersion = "ops-interactive-entrypoint/v1";
    public const string EntrypointStateDirectory = ".runtime/interactive-entrypoints";

    public static string OwnerKey(string ownerSid)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ownerSid.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string PipeName(string ownerSid) =>
        $"CompanyOps.SessionAgent.{OwnerKey(ownerSid)}";

    public static string SnapshotFileName(string projectId, string environment, string ownerSid) =>
        $"interactive-{projectId}-{environment}-{OwnerKey(ownerSid)}.json";

    public static string EntrypointStateFileName(string projectId, string environment, string componentId) =>
        $"{projectId}.{environment}.{componentId}.json";
}

public sealed record InteractiveEntrypointState(
    string ProtocolVersion,
    string ProjectId,
    string Environment,
    string ComponentId,
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    DateTimeOffset UpdatedAt);

public sealed record InteractiveAppControlRequest(
    string ProtocolVersion,
    string RequestId,
    string ProjectId,
    string Environment,
    string ComponentId,
    string ExpectedExecutable,
    string ExpectedWorkingDirectory,
    IReadOnlyList<string> ExpectedArguments,
    ComponentOperationAction Action);

public sealed record InteractiveAppControlResponse(
    string ProtocolVersion,
    string RequestId,
    bool Success,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record InteractiveAppSnapshot(
    string ProtocolVersion,
    string OwnerSid,
    int SessionId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<InteractiveAppProcessSnapshot> Processes);

public sealed record InteractiveAppProcessSnapshot(
    string ProjectId,
    string Environment,
    string ComponentId,
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string State,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAt);

public sealed record AuditEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    string Outcome,
    string? Detail = null,
    JsonElement? Data = null);

public sealed record GitUpdateAuditData(
    string OperationId,
    string ProjectId,
    string Environment,
    GitUpdateAction Action,
    string? FromCommit,
    string? ToCommit,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Steps,
    long DurationMilliseconds,
    bool RolledBack,
    string? ErrorCode = null);
