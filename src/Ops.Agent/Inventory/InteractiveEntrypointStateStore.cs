using System.Text.Json;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed record InteractiveEntrypointStateReadResult(
    bool Exists,
    InteractiveEntrypointState? State,
    string? Error = null);

public sealed class InteractiveEntrypointStateStore(
    OpsPathResolver paths,
    JsonSerializerOptions jsonOptions)
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        paths.Resolve().ManifestDirectory,
        InteractiveSessionProtocol.EntrypointStateDirectory.Replace('/', Path.DirectorySeparatorChar)));

    public async Task<InteractiveEntrypointStateReadResult> ReadAsync(
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(projectId, environment, componentId);
        if (path is null) return new(false, null, "交互入口状态标识无效");
        if (!File.Exists(path)) return new(false, null);
        try
        {
            if (new FileInfo(path).Length > 64 * 1024)
                return new(true, null, "交互入口状态超过大小限制");
            var state = JsonSerializer.Deserialize<InteractiveEntrypointState>(
                await File.ReadAllTextAsync(path, cancellationToken),
                jsonOptions);
            if (state is null ||
                state.ProtocolVersion != InteractiveSessionProtocol.EntrypointStateVersion ||
                state.ProjectId != projectId || state.Environment != environment || state.ComponentId != componentId ||
                !Path.IsPathFullyQualified(state.Executable) || !Path.IsPathFullyQualified(state.WorkingDirectory) ||
                state.Arguments.Count > 32 || state.Arguments.Any(static value =>
                    value.Length > 500 || value.Any(char.IsControl)))
                return new(true, null, "交互入口状态协议或内容无效");
            return new(true, state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(true, null, $"交互入口状态读取失败：{exception.Message}");
        }
    }

    public async Task WriteAsync(InteractiveEntrypointState state, CancellationToken cancellationToken)
    {
        var path = ResolvePath(state.ProjectId, state.Environment, state.ComponentId)
            ?? throw new InvalidDataException("交互入口状态标识无效");
        Directory.CreateDirectory(_root);
        var temporary = path + $".{Guid.CreateVersion7():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, jsonOptions),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(projectId, environment, componentId)
            ?? throw new InvalidDataException("交互入口状态标识无效");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string? ResolvePath(string projectId, string environment, string componentId)
    {
        var fileName = InteractiveSessionProtocol.EntrypointStateFileName(projectId, environment, componentId);
        if (Path.GetFileName(fileName) != fileName || fileName.Any(char.IsControl)) return null;
        var path = Path.GetFullPath(Path.Combine(_root, fileName));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
