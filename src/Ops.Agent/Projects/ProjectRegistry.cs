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

        var projects = new List<ProjectRuntimeView>();
        foreach (var bindingGroup in documents
                     .Where(static item => item.Entry.ManifestKind == "EnvironmentBinding")
                     .Where(item => Equals(String(item.Root, "metadata", "hostId"), hostId))
                     .GroupBy(
                         static item => Key(
                             String(item.Root, "metadata", "projectId"),
                             String(item.Root, "metadata", "environment")),
                         StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
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

        return new ProjectRegistrySnapshot(hostId, DateTimeOffset.UtcNow, projects);
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
            Long(installed, "metadata", "generation"),
            components,
            problems);
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
            else if (installed is null || stateMatches is null)
            {
                ownership = ComponentOwnershipStatus.DeclaredOnly;
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

            result.Add(new ProjectComponentRuntimeView(
                id,
                String(component, "displayName") ?? id,
                kind,
                expectedNativeId,
                installedNativeId,
                ownership,
                String(state, "runtimeState") ?? "unknown",
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
        if (kind != "pm2Legacy" && !Equals(expectedNativeId, installedNativeId))
        {
            return (ComponentOwnershipStatus.Conflict, "InstalledState nativeId 与 EnvironmentBinding 不一致");
        }

        var sourceName = kind switch
        {
            "windowsService" => "windows-services",
            "iisSite" or "staticSite" => "iis",
            "scheduledTask" => "scheduled-tasks",
            "pm2Legacy" => "pm2-legacy",
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

        var inventoryId = kind is "iisSite" or "staticSite" ? $"site:{expectedNativeId}" : expectedNativeId;
        return source.Items.Any(item => Equals(item.Id, inventoryId))
            ? (ComponentOwnershipStatus.Owned, null)
            : (ComponentOwnershipStatus.Missing, "已登记的原生资源未在主机盘点中找到");
    }

    private static string Key(string? projectId, string? environment) => $"{projectId}\n{environment}";

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
