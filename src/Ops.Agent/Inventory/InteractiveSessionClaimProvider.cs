using System.Text.Json.Nodes;
using CompanyOps.Agent.Catalog;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class InteractiveSessionClaimProvider(
    IManifestCatalog manifestCatalog,
    OpsPathResolver pathResolver,
    InteractiveEntrypointStateStore entrypoints) : IInteractiveSessionClaimProvider
{
    public async Task<IReadOnlyList<InteractiveSessionClaim>> GetClaimsAsync(CancellationToken cancellationToken)
    {
        var catalog = await manifestCatalog.InspectAsync(cancellationToken);
        var docs = new List<JsonObject>();
        foreach (var entry in catalog.Entries.Where(static entry => entry.IsValid))
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken)) as JsonObject;
            if (root is not null) docs.Add(root);
        }
        var hostId = pathResolver.Resolve().HostId;
        var bindings = docs.Where(item => String(item, "manifestKind") == "EnvironmentBinding" &&
            string.Equals(String(item, "metadata", "hostId"), hostId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var claims = new List<InteractiveSessionClaim>();
        foreach (var project in docs.Where(static item => String(item, "manifestKind") == "ProjectManifest"))
        {
            var projectId = String(project, "metadata", "id") ?? string.Empty;
            var projectBindings = bindings.Where(item => String(item, "metadata", "projectId") == projectId).ToArray();
            foreach (var component in project["components"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (String(component, "kind") != "interactiveApp") continue;
                claims.Add(await CreateAsync(projectId, hostId, component, projectBindings, cancellationToken));
            }
        }
        return claims.OrderBy(static claim => claim.ProjectId, StringComparer.Ordinal)
            .ThenBy(static claim => claim.Environment, StringComparer.Ordinal)
            .ThenBy(static claim => claim.ComponentId, StringComparer.Ordinal).ToArray();
    }

    private async Task<InteractiveSessionClaim> CreateAsync(
        string projectId,
        string hostId,
        JsonObject component,
        JsonObject[] bindings,
        CancellationToken cancellationToken)
    {
        var componentId = String(component, "id") ?? string.Empty;
        var displayName = String(component, "displayName") ?? componentId;
        if (bindings.Length != 1) return Unbound(bindings.Length == 0 ? "缺少 EnvironmentBinding" : "EnvironmentBinding 不唯一");
        var binding = bindings[0];
        var environment = String(binding, "metadata", "environment") ?? string.Empty;
        var session = binding["interactiveSession"] as JsonObject;
        var root = String(binding, "roots", "install");
        var executable = String(component, "interactive", "executable");
        var workingDirectory = String(component, "interactive", "workingDirectory");
        var arguments = component["interactive"]?["arguments"]?.AsArray()
            .Select(static item => item?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
        var active = await entrypoints.ReadAsync(projectId, environment, componentId, cancellationToken);
        if (active.Exists)
        {
            if (active.State is null || root is null ||
                !TryAbsoluteProjectPath(root, active.State.Executable, out executable) ||
                !TryAbsoluteProjectPath(root, active.State.WorkingDirectory, out workingDirectory))
                return Unbound(active.Error ?? "当前激活的交互入口不在项目安装根目录内");
            arguments = active.State.Arguments.ToArray();
        }
        if (root is null || session is null ||
            !ResolveProjectPath(root, executable, out var resolvedExecutable) ||
            !ResolveProjectPath(root, workingDirectory, out var resolvedWorkingDirectory))
            return Unbound("交互程序路径或 interactiveSession 绑定不完整");
        return new InteractiveSessionClaim(
            projectId, environment, hostId,
            componentId, displayName, resolvedExecutable, resolvedWorkingDirectory, arguments,
            String(session, "ownerSid"), String(session, "snapshotFileName"), String(session, "controlPipeName"),
            session["maxAgeSeconds"]?.GetValue<int>() ?? 30, null);

        InteractiveSessionClaim Unbound(string error) => new(
            projectId, bindings.Length == 1 ? String(bindings[0], "metadata", "environment") ?? string.Empty : string.Empty,
            hostId, componentId, displayName, null, null, [], null, null, null, 30, error);
    }

    private static bool ResolveProjectPath(string root, string? value, out string? result) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? TryAbsoluteProjectPath(root, value, out result)
            : TryProjectPath(root, value, out result);

    private static bool TryProjectPath(string root, string? relative, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            result = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryAbsoluteProjectPath(string root, string? absolute, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(absolute) || !Path.IsPathFullyQualified(absolute)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(absolute);
            if (!string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            result = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? String(JsonObject? root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var segment in path) node = node?[segment];
        return node?.GetValue<string>();
    }
}
