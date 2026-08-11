using System.Text.Json.Nodes;
using CompanyOps.Agent.Catalog;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class LegacyPm2ClaimProvider(
    IManifestCatalog manifestCatalog,
    OpsPathResolver pathResolver) : ILegacyPm2ClaimProvider
{
    public async Task<IReadOnlyList<LegacyPm2Claim>> GetClaimsAsync(
        CancellationToken cancellationToken)
    {
        var catalog = await manifestCatalog.InspectAsync(cancellationToken);
        var documents = new List<(ManifestCatalogEntry Entry, JsonObject Root)>();
        foreach (var entry in catalog.Entries.Where(static entry => entry.IsValid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken))
                as JsonObject;
            if (root is not null)
            {
                documents.Add((entry, root));
            }
        }

        var currentHostId = pathResolver.Resolve().HostId;
        var bindings = documents
            .Where(static document => document.Entry.ManifestKind == "EnvironmentBinding")
            .Select(static document => document.Root)
            .Where(
                binding =>
                    string.Equals(
                        GetString(binding, "metadata", "hostId"),
                        currentHostId,
                        StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var claims = new List<LegacyPm2Claim>();
        foreach (var projectDocument in documents.Where(
                     static document => document.Entry.ManifestKind == "ProjectManifest"))
        {
            var project = projectDocument.Root;
            var projectId = GetString(project, "metadata", "id") ?? string.Empty;
            var projectBindings = bindings
                .Where(
                    binding =>
                        string.Equals(
                            GetString(binding, "metadata", "projectId"),
                            projectId,
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var componentNode in project["components"]?.AsArray() ?? [])
            {
                if (componentNode is not JsonObject component ||
                    !string.Equals(
                        component["kind"]?.GetValue<string>(),
                        "pm2Legacy",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                claims.Add(
                    CreateClaim(
                        projectId,
                        currentHostId,
                        component,
                        projectBindings));
            }
        }

        return claims
            .OrderBy(static claim => claim.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static claim => claim.ComponentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LegacyPm2Claim CreateClaim(
        string projectId,
        string currentHostId,
        JsonObject component,
        JsonObject[] projectBindings)
    {
        var componentId = component["id"]?.GetValue<string>() ?? string.Empty;
        var displayName = component["displayName"]?.GetValue<string>() ?? componentId;
        var processName = component["pm2"]?["name"]?.GetValue<string>() ?? string.Empty;

        if (projectBindings.Length == 0)
        {
            return Unbound("缺少当前主机的 EnvironmentBinding");
        }

        if (projectBindings.Length > 1)
        {
            return Unbound("当前主机存在多个 EnvironmentBinding，拒绝猜测");
        }

        var binding = projectBindings[0];
        var environment = GetString(binding, "metadata", "environment") ?? string.Empty;
        var installRoot = GetString(binding, "roots", "install");
        var legacyPm2 = binding["legacyPm2"] as JsonObject;
        if (installRoot is null || legacyPm2 is null)
        {
            return Unbound("EnvironmentBinding 缺少 roots.install 或 legacyPm2");
        }

        var ownerSid = legacyPm2["ownerSid"]?.GetValue<string>();
        var snapshotFileName = legacyPm2["snapshotFileName"]?.GetValue<string>();
        var controlPipeName = legacyPm2["controlPipeName"]?.GetValue<string>();
        var maxAgeSeconds = legacyPm2["maxAgeSeconds"]?.GetValue<int>() ?? 30;
        var cwd = component["pm2"]?["cwd"]?.GetValue<string>();
        var script = component["pm2"]?["script"]?.GetValue<string>();

        if (!TryResolveProjectPath(installRoot, cwd, out var expectedCwd) ||
            !TryResolveProjectPath(installRoot, script, out var expectedScript))
        {
            return Unbound("PM2 cwd/script 无法安全解析到安装根目录内");
        }

        return new LegacyPm2Claim(
            projectId,
            environment,
            currentHostId,
            componentId,
            displayName,
            processName,
            expectedCwd,
            expectedScript,
            ownerSid,
            snapshotFileName,
            controlPipeName,
            maxAgeSeconds,
            null);

        LegacyPm2Claim Unbound(string error) =>
            new(
                projectId,
                projectBindings.Length == 1
                    ? GetString(projectBindings[0], "metadata", "environment") ?? string.Empty
                    : string.Empty,
                currentHostId,
                componentId,
                displayName,
                processName,
                null,
                null,
                null,
                null,
                null,
                30,
                error);
    }

    private static bool TryResolveProjectPath(
        string root,
        string? relativePath,
        out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    private static string? GetString(JsonObject root, string objectName, string propertyName) =>
        root[objectName]?[propertyName]?.GetValue<string>();
}
