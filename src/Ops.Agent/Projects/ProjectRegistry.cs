using System.Text.Json.Nodes;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Projects;

public sealed class ProjectRegistry(OpsPathResolver pathResolver) : IProjectRegistry
{
    public async Task<ProjectRegistrySnapshot> BuildAsync(
        ManifestCatalogSnapshot catalog,
        InventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        var documents = new List<(ManifestCatalogEntry Entry, JsonObject Root)>();
        foreach (var entry in catalog.Entries.Where(static item => item.IsValid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken)) as JsonObject;
            if (root is not null)
            {
                documents.Add((entry, root));
            }
        }

        var hostId = pathResolver.Resolve().HostId;
        var manifests = documents
            .Where(static item => item.Entry.ManifestKind == "ProjectManifest")
            .GroupBy(static item => String(item.Root, "metadata", "id") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var installedStates = documents
            .Where(static item => item.Entry.ManifestKind == "InstalledState")
            .Where(item => Equals(String(item.Root, "metadata", "hostId"), hostId))
            .GroupBy(
                static item => Key(
                    String(item.Root, "metadata", "projectId"),
                    String(item.Root, "metadata", "environment")),
                StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        var bindingGroups = documents
            .Where(static item => item.Entry.ManifestKind == "EnvironmentBinding")
            .Where(item => Equals(String(item.Root, "metadata", "hostId"), hostId))
            .GroupBy(
                static item => Key(
                    String(item.Root, "metadata", "projectId"),
                    String(item.Root, "metadata", "environment")),
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var installRoots = bindingGroups
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => String(group.Single().Root, "roots", "install"),
                StringComparer.Ordinal);
        var projects = new List<ProjectRuntimeView>();
        foreach (var bindingGroup in bindingGroups)
        {
            var binding = bindingGroup.First().Root;
            var projectId = String(binding, "metadata", "projectId") ?? "unknown";
            var environment = String(binding, "metadata", "environment") ?? "unknown";
            manifests.TryGetValue(projectId, out var manifestMatches);
            installedStates.TryGetValue(bindingGroup.Key, out var installedMatches);
            projects.Add(BuildProject(
                projectId,
                environment,
                bindingGroup.Select(static item => item.Root).ToArray(),
                manifestMatches ?? [],
                installedMatches ?? [],
                inventory));
        }

        return new ProjectRegistrySnapshot(
            hostId,
            DateTimeOffset.UtcNow,
            ApplyInstallRootConflicts(
                ApplyGlobalNativeOwnershipConflicts(projects),
                installRoots));
    }

    private static IReadOnlyList<ProjectRuntimeView> ApplyInstallRootConflicts(
        IReadOnlyList<ProjectRuntimeView> projects,
        IReadOnlyDictionary<string, string?> installRoots)
    {
        var normalizedRoots = installRoots
            .Select(pair => new { ProjectKey = pair.Key, Root = NormalizePath(pair.Value) })
            .Where(static item => item.Root is not null)
            .Select(static item => new { item.ProjectKey, Root = item.Root! })
            .ToArray();
        var conflicts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < normalizedRoots.Length; index++)
        {
            for (var otherIndex = index + 1; otherIndex < normalizedRoots.Length; otherIndex++)
            {
                var left = normalizedRoots[index];
                var right = normalizedRoots[otherIndex];
                if (!PathsOverlap(left.Root, right.Root))
                {
                    continue;
                }

                AddConflict(
                    left.ProjectKey,
                    $"项目安装根目录 {left.Root} 与 {right.Root} 重叠",
                    conflicts);
                AddConflict(
                    right.ProjectKey,
                    $"项目安装根目录 {right.Root} 与 {left.Root} 重叠",
                    conflicts);
            }
        }

        if (conflicts.Count == 0)
        {
            return projects;
        }

        return projects.Select(project =>
        {
            if (!conflicts.TryGetValue(Key(project.ProjectId, project.Environment), out var details))
            {
                return project;
            }

            return project with
            {
                Status = ProjectBindingStatus.Conflict,
                Problems = project.Problems.Concat(details).Distinct(StringComparer.Ordinal).ToArray()
            };
        }).ToArray();
    }

    private static void AddConflict(
        string projectKey,
        string detail,
        Dictionary<string, List<string>> conflicts)
    {
        if (!conflicts.TryGetValue(projectKey, out var details))
        {
            details = [];
            conflicts.Add(projectKey, details);
        }

        details.Add(detail);
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPrefix = left + Path.DirectorySeparatorChar;
        var rightPrefix = right + Path.DirectorySeparatorChar;
        return left.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) ||
               right.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ProjectRuntimeView> ApplyGlobalNativeOwnershipConflicts(
        IReadOnlyList<ProjectRuntimeView> projects)
    {
        var claims = projects
            .SelectMany(project => project.Components
                .Select(component => new NativeClaim(project, component, ResourceKey(component))))
            .Where(static claim => claim.ResourceKey is not null)
            .GroupBy(static claim => claim.ResourceKey!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .SelectMany(group => group.Select(claim => new
            {
                ProjectKey = Key(claim.Project.ProjectId, claim.Project.Environment),
                claim.Component.ComponentId,
                Detail = $"主机原生资源 {claim.Component.ExpectedNativeId} 被多个项目或组件声明"
            }))
            .GroupBy(static item => item.ProjectKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToDictionary(
                    static item => item.ComponentId,
                    static item => item.Detail,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        if (claims.Count == 0)
        {
            return projects;
        }

        return projects.Select(project =>
        {
            if (!claims.TryGetValue(Key(project.ProjectId, project.Environment), out var componentConflicts))
            {
                return project;
            }

            var components = project.Components.Select(component =>
                componentConflicts.TryGetValue(component.ComponentId, out var detail)
                    ? component with { Ownership = ComponentOwnershipStatus.Conflict, Detail = detail }
                    : component).ToArray();
            var problems = project.Problems
                .Concat(componentConflicts.Select(static pair => $"组件 {pair.Key}: {pair.Value}"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return project with
            {
                Status = ProjectBindingStatus.Conflict,
                Components = components,
                Problems = problems
            };
        }).ToArray();
    }

    private static string? ResourceKey(ProjectComponentRuntimeView component)
    {
        if (string.IsNullOrWhiteSpace(component.ExpectedNativeId) || component.Kind is "pm2Legacy" or "interactiveApp")
        {
            return null;
        }

        var adapter = component.Kind switch
        {
            "windowsService" => "scm",
            "iisSite" or "staticSite" => "iis",
            "scheduledTask" => "task",
            "interactiveApp" => "interactive-session",
            _ => null
        };
        return adapter is null ? null : $"{adapter}:{component.ExpectedNativeId}";
    }

    private static ProjectRuntimeView BuildProject(
        string projectId,
        string environment,
        IReadOnlyList<JsonObject> bindings,
        IReadOnlyList<(ManifestCatalogEntry Entry, JsonObject Root)> manifests,
        IReadOnlyList<(ManifestCatalogEntry Entry, JsonObject Root)> installedStates,
        InventorySnapshot inventory)
    {
        var problems = new List<string>();
        if (bindings.Count != 1)
        {
            problems.Add($"同一项目/环境/主机存在 {bindings.Count} 份 EnvironmentBinding");
        }

        if (manifests.Count != 1)
        {
            problems.Add(manifests.Count == 0 ? "缺少 ProjectManifest" : "ProjectManifest 重复");
        }

        if (installedStates.Count > 1)
        {
            problems.Add("InstalledState 重复，拒绝猜测当前代次");
        }

        var manifest = manifests.FirstOrDefault().Root;
        var installed = installedStates.FirstOrDefault().Root;
        var binding = bindings[0];
        var displayName = String(manifest, "metadata", "displayName") ?? projectId;
        var components = manifest is null
            ? []
            : BuildComponents(manifest, binding, installed, inventory, problems);

        var status = problems.Count > 0 || components.Any(static component => component.Ownership == ComponentOwnershipStatus.Conflict)
            ? ProjectBindingStatus.Conflict
            : installed is null
                ? ProjectBindingStatus.Declared
                : components.All(static component => component.Ownership == ComponentOwnershipStatus.Owned)
                    ? ProjectBindingStatus.Installed
                    : ProjectBindingStatus.Degraded;

        return new ProjectRuntimeView(
            projectId,
            displayName,
            environment,
            status,
            String(installed, "release", "version"),
            Long(installed, "metadata", "generation") ?? Long(binding, "metadata", "revision"),
            components,
            problems)
        {
            InstallRoot = String(binding, "roots", "install"),
            HasInstalledState = installed is not null,
            GitUpdateEnabled = string.Equals(
                String(manifest, "update", "source", "kind"),
                "gitFastForward",
                StringComparison.Ordinal)
        };
    }

    private static IReadOnlyList<ProjectComponentRuntimeView> BuildComponents(
        JsonObject manifest,
        JsonObject binding,
        JsonObject? installed,
        InventorySnapshot inventory,
        List<string> projectProblems)
    {
        var bindings = binding["componentBindings"]?.AsArray()
            .OfType<JsonObject>()
            .GroupBy(static item => String(item, "componentId") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject[]>(StringComparer.Ordinal);
        var states = installed?["components"]?.AsArray()
            .OfType<JsonObject>()
            .GroupBy(static item => String(item, "componentId") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject[]>(StringComparer.Ordinal);

        var result = new List<ProjectComponentRuntimeView>();
        foreach (var component in manifest["components"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = String(component, "id") ?? "unknown";
            var kind = String(component, "kind") ?? "unknown";
            bindings.TryGetValue(id, out var bindingMatches);
            states.TryGetValue(id, out var stateMatches);
            var expectedNativeId = bindingMatches?.Length == 1
                ? String(bindingMatches[0], "nativeName") ?? string.Empty
                : string.Empty;
            var state = stateMatches?.Length == 1 ? stateMatches[0] : null;
            var installedNativeId = String(state, "nativeId");
            var detail = default(string);
            ComponentOwnershipStatus ownership;

            if (bindingMatches?.Length != 1)
            {
                ownership = ComponentOwnershipStatus.Conflict;
                detail = bindingMatches is null ? "缺少 componentBinding" : "componentBinding 重复";
            }
            else if (installed is null)
            {
                installedNativeId = expectedNativeId;
                (ownership, detail) = CorrelateRuntime(
                    kind,
                    expectedNativeId,
                    installedNativeId,
                    component,
                    binding,
                    inventory);
            }
            else if (stateMatches is null)
            {
                installedNativeId = expectedNativeId;
                (ownership, detail) = CorrelateRuntime(
                    kind,
                    expectedNativeId,
                    installedNativeId,
                    component,
                    binding,
                    inventory);
                if (ownership == ComponentOwnershipStatus.Owned)
                {
                    detail = "组件由增量声明新增，当前原生资源归属已验证";
                }
            }
            else if (stateMatches.Length != 1 || !Equals(String(state, "kind"), kind))
            {
                ownership = ComponentOwnershipStatus.Conflict;
                detail = stateMatches.Length != 1 ? "InstalledState 组件重复" : "组件 kind 与声明不一致";
            }
            else
            {
                (ownership, detail) = CorrelateRuntime(kind, expectedNativeId, installedNativeId, component, binding, inventory);
            }

            if (ownership == ComponentOwnershipStatus.Conflict)
            {
                projectProblems.Add($"组件 {id}: {detail}");
            }

            var runtimeState = kind == "interactiveApp"
                ? ResolveInteractiveRuntimeState(binding, id, inventory)
                : ResolveRuntimeState(kind, expectedNativeId, inventory);
            result.Add(new ProjectComponentRuntimeView(
                id,
                String(component, "displayName") ?? id,
                kind,
                expectedNativeId,
                installedNativeId,
                ownership,
                runtimeState ?? String(state, "runtimeState") ?? "unknown",
                String(state, "healthState") ?? "unknown",
                detail));
        }

        return result;
    }

    private static (ComponentOwnershipStatus Status, string? Detail) CorrelateRuntime(
        string kind,
        string expectedNativeId,
        string? installedNativeId,
        JsonObject component,
        JsonObject binding,
        InventorySnapshot inventory)
    {
        if (kind is not ("pm2Legacy" or "interactiveApp") && !Equals(expectedNativeId, installedNativeId))
        {
            return (ComponentOwnershipStatus.Conflict, "InstalledState nativeId 与 EnvironmentBinding 不一致");
        }

        var sourceName = kind switch
        {
            "windowsService" => "windows-services",
            "iisSite" or "staticSite" => "iis",
            "scheduledTask" => "scheduled-tasks",
            "pm2Legacy" => "pm2-legacy",
            "interactiveApp" => "interactive-apps",
            _ => string.Empty
        };
        var source = inventory.Sections.FirstOrDefault(section => Equals(section.Source, sourceName));
        if (source is null || source.Status != InventorySourceStatus.Available)
        {
            return (ComponentOwnershipStatus.Unknown, source?.Detail ?? "盘点源不可用");
        }

        if (kind == "pm2Legacy")
        {
            var projectId = String(binding, "metadata", "projectId");
            var environment = String(binding, "metadata", "environment");
            var componentId = String(component, "id");
            var item = source.Items.SingleOrDefault(item => Equals(item.Id, $"{projectId}/{environment}/{componentId}"));
            if (item is null)
            {
                return (ComponentOwnershipStatus.Missing, "PM2 归属快照中没有该组件");
            }

            var snapshotPmId = item.Metadata.GetValueOrDefault("pmId");
            if (item.State == "Matched" &&
                !string.Equals(installedNativeId, $"pm_id:{snapshotPmId}", StringComparison.Ordinal))
            {
                return (ComponentOwnershipStatus.Conflict, "InstalledState pm_id 与当前唯一归属快照不一致");
            }

            return item.State == "Matched"
                ? (ComponentOwnershipStatus.Owned, item.Metadata.GetValueOrDefault("detail"))
                : (item.State.Contains("Conflict", StringComparison.Ordinal)
                    ? ComponentOwnershipStatus.Conflict
                    : ComponentOwnershipStatus.Unknown, item.Metadata.GetValueOrDefault("detail"));
        }

        if (kind == "interactiveApp")
        {
            var projectId = String(binding, "metadata", "projectId");
            var environment = String(binding, "metadata", "environment");
            var componentId = String(component, "id");
            var item = source.Items.SingleOrDefault(item => Equals(item.Id, $"{projectId}/{environment}/{componentId}"));
            if (item is null) return (ComponentOwnershipStatus.Missing, "Session Agent 快照中没有该组件");
            return item.State == "Matched"
                ? (ComponentOwnershipStatus.Owned, item.Metadata.GetValueOrDefault("detail"))
                : (item.State == "Conflict" ? ComponentOwnershipStatus.Conflict : ComponentOwnershipStatus.Unknown,
                    item.Metadata.GetValueOrDefault("detail"));
        }

        var inventoryId = kind is "iisSite" or "staticSite" ? $"site:{expectedNativeId}" : expectedNativeId;
        var inventoryMatches = source.Items.Where(item => Equals(item.Id, inventoryId)).ToArray();
        if (inventoryMatches.Length != 1)
        {
            return inventoryMatches.Length == 0
                ? (ComponentOwnershipStatus.Missing, "已登记的原生资源未在主机盘点中找到")
                : (ComponentOwnershipStatus.Conflict, "已登记的原生资源在主机盘点中不唯一");
        }

        if (kind == "windowsService" && !string.IsNullOrWhiteSpace(String(binding, "roots", "install")))
        {
            var installRoot = String(binding, "roots", "install");
            if (!WindowsServiceBelongsToProject(inventoryMatches[0], installRoot, out var detail))
            {
                return (ComponentOwnershipStatus.Conflict, detail);
            }
        }

        return (ComponentOwnershipStatus.Owned, null);
    }

    private static string? ResolveRuntimeState(
        string kind,
        string expectedNativeId,
        InventorySnapshot inventory)
    {
        var sourceName = kind switch
        {
            "windowsService" => "windows-services",
            "iisSite" or "staticSite" => "iis",
            "scheduledTask" => "scheduled-tasks",
            _ => null
        };
        if (sourceName is null)
        {
            return null;
        }

        var inventoryId = kind is "iisSite" or "staticSite" ? $"site:{expectedNativeId}" : expectedNativeId;
        var item = inventory.Sections
            .SingleOrDefault(section => section.Source == sourceName && section.Status == InventorySourceStatus.Available)?
            .Items.SingleOrDefault(candidate => Equals(candidate.Id, inventoryId));
        return item?.State.ToLowerInvariant();
    }

    private static string? ResolveInteractiveRuntimeState(
        JsonObject binding,
        string componentId,
        InventorySnapshot inventory)
    {
        var projectId = String(binding, "metadata", "projectId");
        var environment = String(binding, "metadata", "environment");
        return inventory.Sections
            .SingleOrDefault(section => section.Source == "interactive-apps" && section.Status == InventorySourceStatus.Available)?
            .Items.SingleOrDefault(item => Equals(item.Id, $"{projectId}/{environment}/{componentId}"))?
            .Metadata.GetValueOrDefault("runtimeStatus")?.ToLowerInvariant();
    }

    private static bool WindowsServiceBelongsToProject(
        InventoryItem item,
        string? installRoot,
        out string detail)
    {
        detail = string.Empty;
        var executable = ExtractExecutablePath(item.Metadata.GetValueOrDefault("binaryPath"));
        if (!PathInsideRoot(executable, installRoot))
        {
            detail = "Windows Service ImagePath 不在项目目录内";
            return false;
        }

        var nssmApplication = item.Metadata.GetValueOrDefault("nssmApplication");
        var nssmDirectory = item.Metadata.GetValueOrDefault("nssmAppDirectory");
        var nssmParameters = item.Metadata.GetValueOrDefault("nssmAppParameters");
        if (nssmApplication is null && nssmDirectory is null && nssmParameters is null)
        {
            return true;
        }

        if (!PathInsideRoot(nssmApplication, installRoot) ||
            !PathInsideRoot(nssmDirectory, installRoot))
        {
            detail = "NSSM Application 或 AppDirectory 未归属项目目录";
            return false;
        }

        return true;
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var value = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? value[..(executableEnd + 4)] : value.Split(' ', 2)[0];
    }

    private static bool PathInsideRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathEqualsRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"'))),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool CommandLineReferencesRoot(string? commandLine, string? root)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return commandLine.IndexOf(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Key(string? projectId, string? environment) => $"{projectId}\n{environment}";

    private sealed record NativeClaim(
        ProjectRuntimeView Project,
        ProjectComponentRuntimeView Component,
        string? ResourceKey);

    private static bool Equals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? String(JsonObject? root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
        {
            node = node?[segment];
        }

        return node?.GetValue<string>();
    }

    private static long? Long(JsonObject? root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
        {
            node = node?[segment];
        }

        return node?.GetValue<long>();
    }
}
