namespace CompanyOps.SessionAgent;

public sealed class SessionAgentOptions
{
    public const string SectionName = "SessionAgent";

    public string ManifestDirectory { get; set; } = string.Empty;
    public string SnapshotDirectory { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public int SnapshotIntervalSeconds { get; set; } = 10;
}
