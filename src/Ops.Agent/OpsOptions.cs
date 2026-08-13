using Microsoft.Extensions.Options;

namespace CompanyOps.Agent;

public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    public string HostId { get; set; } = string.Empty;

    public string ManifestDirectory { get; set; } = string.Empty;

    public string StateDirectory { get; set; } = string.Empty;

    public string Pm2SnapshotDirectory { get; set; } = string.Empty;

    public string InteractiveSnapshotDirectory { get; set; } = string.Empty;

    public string PipeName { get; set; } = "CompanyOps.Agent.v1";

    public int InventoryIntervalSeconds { get; set; } = 30;

    public bool EnableMutations { get; set; }

    public bool EnableExistingServiceOperations { get; set; } = true;

    public bool EnableInteractiveSessionOperations { get; set; } = true;

    public bool EnableExistingGitUpdates { get; set; } = true;

    public string GitExecutablePath { get; set; } = string.Empty;

    public string[] AllowedProjectInstallRoots { get; set; } = [];

    public string[] AllowedClientSids { get; set; } = [];

    public static bool HasSafeAllowedProjectInstallRoots(OpsOptions options)
    {
        if (!options.EnableMutations)
        {
            return true;
        }

        if (options.AllowedProjectInstallRoots is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            return options.AllowedProjectInstallRoots.All(path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
                var fullPath = Path.GetFullPath(expanded);
                var pathRoot = Path.GetPathRoot(fullPath);
                return Path.IsPathFullyQualified(fullPath) &&
                       pathRoot is not null &&
                       !string.Equals(
                           Path.TrimEndingDirectorySeparator(fullPath),
                           Path.TrimEndingDirectorySeparator(pathRoot),
                           StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed record ResolvedOpsPaths(
    string HostId,
    string ManifestDirectory,
    string StateDirectory,
    string StateDatabasePath,
    string Pm2SnapshotDirectory,
    string InteractiveSnapshotDirectory,
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
        var interactiveSnapshotDirectory = ResolveAbsolutePath(
            _options.InteractiveSnapshotDirectory,
            Path.Combine(stateDirectory, "interactive-snapshots"));

        var hostId = string.IsNullOrWhiteSpace(_options.HostId)
            ? Environment.MachineName
            : _options.HostId.Trim();

        return new ResolvedOpsPaths(
            hostId,
            manifestDirectory,
            stateDirectory,
            Path.Combine(stateDirectory, "ops-agent.db"),
            pm2SnapshotDirectory,
            interactiveSnapshotDirectory,
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
