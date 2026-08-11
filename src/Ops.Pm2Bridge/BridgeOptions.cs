namespace CompanyOps.Pm2Bridge;

public sealed class BridgeOptions
{
    public const string SectionName = "Pm2Bridge";

    public string PipeName { get; set; } = "CompanyOps.Pm2Bridge.v1";

    public string ManifestDirectory { get; set; } = string.Empty;

    public string SnapshotDirectory { get; set; } = string.Empty;

    public string NodeExecutablePath { get; set; } = string.Empty;

    public string Pm2CliPath { get; set; } = string.Empty;

    public int SnapshotIntervalSeconds { get; set; } = 10;
}
