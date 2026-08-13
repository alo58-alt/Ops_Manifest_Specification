using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.SessionAgent;

public sealed record InteractiveClaim(
    string ProjectId,
    string Environment,
    string ComponentId,
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string StartPolicy,
    int StopTimeoutSeconds,
    bool AllowForceTerminate,
    string SnapshotFileName);

public sealed class InteractiveClaimReader(
    IOptions<SessionAgentOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<InteractiveClaimReader> logger)
{
    public async Task<IReadOnlyList<InteractiveClaim>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.Value.ManifestDirectory)) return [];
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前用户 SID");
        var documents = new List<JsonObject>();
        foreach (var path in Directory.EnumerateFiles(options.Value.ManifestDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 4 * 1024 * 1024) continue;
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject;
                if (root is not null) documents.Add(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning("跳过无法读取的运维声明 {Path}：{Message}", path, exception.Message);
            }
        }

        var manifests = documents
            .Where(static item => String(item, "manifestKind") == "ProjectManifest")
            .GroupBy(static item => String(item, "metadata", "id") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<InteractiveClaim>();
        foreach (var binding in documents.Where(static item => String(item, "manifestKind") == "EnvironmentBinding"))
        {
            var session = binding["interactiveSession"] as JsonObject;
            if (String(session, "ownerSid") != ownerSid ||
                String(session, "controlPipeName") != options.Value.PipeName) continue;
            var projectId = String(binding, "metadata", "projectId") ?? string.Empty;
            var environment = String(binding, "metadata", "environment") ?? string.Empty;
            var installRoot = String(binding, "roots", "install");
            var snapshotFile = String(session, "snapshotFileName");
            if (!manifests.TryGetValue(projectId, out var matches) || matches.Length != 1 ||
                installRoot is null || snapshotFile is null || Path.GetFileName(snapshotFile) != snapshotFile) continue;

            foreach (var component in matches[0]["components"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (String(component, "kind") != "interactiveApp") continue;
                var interactive = component["interactive"] as JsonObject;
                var componentId = String(component, "id") ?? string.Empty;
                var executableValue = String(interactive, "executable");
                var workingDirectoryValue = String(interactive, "workingDirectory");
                var arguments = interactive?["arguments"]?.AsArray()
                    .Select(static item => item?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
                var active = await ReadActiveStateAsync(
                    options.Value.ManifestDirectory,
                    projectId,
                    environment,
                    componentId,
                    cancellationToken);
                if (active.Exists)
                {
                    if (active.State is null) continue;
                    executableValue = active.State.Executable;
                    workingDirectoryValue = active.State.WorkingDirectory;
                    arguments = active.State.Arguments.ToArray();
                }
                if (!ResolveProjectPath(installRoot, executableValue, out var executable) ||
                    !ResolveProjectPath(installRoot, workingDirectoryValue, out var workingDirectory) ||
                    !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (arguments.Length > 32 || arguments.Any(static value => value.Length > 500 || value.Any(char.IsControl))) continue;
                result.Add(new InteractiveClaim(
                    projectId,
                    environment,
                    componentId,
                    executable!,
                    workingDirectory!,
                    arguments,
                    String(interactive, "startPolicy") ?? "manual",
                    Math.Clamp(interactive?["stopTimeoutSeconds"]?.GetValue<int>() ?? 10, 1, 60),
                    interactive?["allowForceTerminate"]?.GetValue<bool>() ?? false,
                    snapshotFile));
            }
        }
        return result
            .GroupBy(static claim => $"{claim.ProjectId}\n{claim.Environment}\n{claim.ComponentId}", StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .ToArray();
    }

    private async Task<(bool Exists, InteractiveEntrypointState? State)> ReadActiveStateAsync(
        string manifestDirectory,
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(
            manifestDirectory,
            InteractiveSessionProtocol.EntrypointStateDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var fileName = InteractiveSessionProtocol.EntrypointStateFileName(projectId, environment, componentId);
        if (Path.GetFileName(fileName) != fileName) return (true, null);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return (false, null);
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return (true, null);
            var state = JsonSerializer.Deserialize<InteractiveEntrypointState>(
                await File.ReadAllTextAsync(path, cancellationToken),
                jsonOptions);
            return state is not null &&
                state.ProtocolVersion == InteractiveSessionProtocol.EntrypointStateVersion &&
                state.ProjectId == projectId && state.Environment == environment && state.ComponentId == componentId &&
                Path.IsPathFullyQualified(state.Executable) && Path.IsPathFullyQualified(state.WorkingDirectory) &&
                state.Arguments.Count <= 32 && !state.Arguments.Any(static value =>
                    value.Length > 500 || value.Any(char.IsControl))
                ? (true, state)
                : (true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("跳过无效的交互入口状态 {Path}：{Message}", path, exception.Message);
            return (true, null);
        }
    }

    private static bool ResolveProjectPath(string root, string? value, out string? result) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? TryAbsoluteProjectPath(root, value, out result)
            : TryProjectPath(root, value, out result);

    private static bool TryProjectPath(string root, string? relativePath, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            resolved = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryAbsoluteProjectPath(string root, string? absolutePath, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(absolutePath);
            if (!string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            resolved = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? String(JsonObject? root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var segment in path) node = node?[segment];
        return node?.GetValue<string>();
    }
}
