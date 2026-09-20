using System.Text.Json;
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

        if (!TryResolveCurrentReleaseIdentity(installRoot, componentId, processName, out var releaseCwd, out var releaseScript, out var releaseError))
        {
            return Unbound(releaseError!);
        }
        if (releaseCwd is not null && releaseScript is not null)
        {
            expectedCwd = releaseCwd;
            expectedScript = releaseScript;
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

    private static bool TryResolveCurrentReleaseIdentity(
        string installRoot,
        string componentId,
        string processName,
        out string? cwd,
        out string? script,
        out string? error)
    {
        cwd = null;
        script = null;
        error = null;
        var pointerPath = Path.Combine(installRoot, "current.release.json");
        if (!File.Exists(pointerPath))
        {
            return true;
        }

        try
        {
            var pointer = JsonNode.Parse(File.ReadAllText(pointerPath)) as JsonObject;
            var currentPath = pointer?["currentPath"]?.GetValue<string>();
            var releasesRoot = Path.GetFullPath(Path.Combine(installRoot, "releases"));
            if (string.IsNullOrWhiteSpace(currentPath) || !IsUnderRoot(currentPath, releasesRoot))
            {
                error = "current.release.json 的 PM2 release 路径不可信";
                return false;
            }

            var manifestPath = Path.Combine(currentPath, ".companyops", "release-manifest.json");
            var release = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            var payloads = release?["componentPayloads"]?.AsArray().OfType<JsonObject>()
                .Where(item => item["componentId"]?.GetValue<string>() == componentId).ToArray() ?? [];
            if (payloads.Length != 1 || payloads[0]["pm2"] is not JsonObject pm2 ||
                pm2["name"]?.GetValue<string>() != processName)
            {
                error = "当前 release 缺少唯一且同名的 PM2 发布身份";
                return false;
            }

            var artifactId = payloads[0]["artifactId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(artifactId))
            {
                error = "当前 release 的 PM2 artifactId 无效";
                return false;
            }
            var artifactRoot = Path.Combine(currentPath, artifactId);
            if (!TryResolveProjectPath(artifactRoot, pm2["cwd"]?.GetValue<string>(), out cwd) ||
                !TryResolveProjectPath(artifactRoot, pm2["script"]?.GetValue<string>(), out script))
            {
                error = "当前 release 的 PM2 cwd/script 路径越界";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            error = $"当前 PM2 release 身份读取失败：{exception.Message}";
            return false;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonObject root, string objectName, string propertyName) =>
        root[objectName]?[propertyName]?.GetValue<string>();
}
