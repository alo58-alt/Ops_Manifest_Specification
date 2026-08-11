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
    IReadOnlyList<string> Problems);

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

public sealed record AuditEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    string Outcome,
    string? Detail = null);
