using Microsoft.Extensions.Options;

namespace CompanyOps.Agent;

public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    public string HostId { get; set; } = string.Empty;

    public string ManifestDirectory { get; set; } = string.Empty;

    public string StateDirectory { get; set; } = string.Empty;

    public string Pm2SnapshotDirectory { get; set; } = string.Empty;

    public string PipeName { get; set; } = "CompanyOps.Agent.v1";

    public int InventoryIntervalSeconds { get; set; } = 30;

    public bool EnableMutations { get; set; }

    public string[] AllowedClientSids { get; set; } = [];
}

public sealed record ResolvedOpsPaths(
    string HostId,
    string ManifestDirectory,
    string StateDirectory,
    string StateDatabasePath,
    string Pm2SnapshotDirectory,
    string SchemaDirectory);

public sealed class OpsPathResolver(IOptions<OpsOptions> options)
{
    private readonly OpsOptions _options = options.Value;

    public ResolvedOpsPaths Resolve()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var baseDirectory = Path.Combine(programData, "CompanyOps");
        var manifestDirectory = ResolveAbsolutePath(
            _options.ManifestDirectory,
            Path.Combine(baseDirectory, "manifests"));
        var stateDirectory = ResolveAbsolutePath(
            _options.StateDirectory,
            Path.Combine(baseDirectory, "Agent"));
        var pm2SnapshotDirectory = ResolveAbsolutePath(
            _options.Pm2SnapshotDirectory,
            Path.Combine(stateDirectory, "pm2-snapshots"));

        var hostId = string.IsNullOrWhiteSpace(_options.HostId)
            ? Environment.MachineName
            : _options.HostId.Trim();

        return new ResolvedOpsPaths(
            hostId,
            manifestDirectory,
            stateDirectory,
            Path.Combine(stateDirectory, "ops-agent.db"),
            pm2SnapshotDirectory,
            Path.Combine(AppContext.BaseDirectory, "schemas"));
    }

    private static string ResolveAbsolutePath(string configuredPath, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? fallback
            : Environment.ExpandEnvironmentVariables(configuredPath.Trim());

        return Path.GetFullPath(candidate);
    }
}
