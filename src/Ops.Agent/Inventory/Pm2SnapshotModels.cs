namespace CompanyOps.Agent.Inventory;

public sealed record Pm2Snapshot(
    string ProtocolVersion,
    string OwnerSid,
    DateTimeOffset CapturedAt,
    int DaemonPid,
    IReadOnlyList<Pm2ProcessSnapshot> Processes);

public sealed record Pm2ProcessSnapshot(
    string Name,
    int PmId,
    string Cwd,
    string Script,
    string Status,
    int Pid,
    int RestartCount);

public sealed record LegacyPm2Claim(
    string ProjectId,
    string Environment,
    string HostId,
    string ComponentId,
    string DisplayName,
    string ProcessName,
    string? ExpectedCwd,
    string? ExpectedScript,
    string? OwnerSid,
    string? SnapshotFileName,
    string? ControlPipeName,
    int MaxAgeSeconds,
    string? BindingError);

public enum Pm2OwnershipState
{
    Matched,
    Missing,
    Conflict,
    Unbound,
    SnapshotUnavailable,
    SnapshotStale
}

public sealed record Pm2OwnershipResult(
    Pm2OwnershipState State,
    string Detail,
    Pm2ProcessSnapshot? Process = null);
