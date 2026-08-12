using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;

namespace CompanyOps.Agent.Operations;

public sealed record HealthGateResult(bool Success, string? Detail = null);

public interface IComponentHealthGate
{
    Task<HealthGateResult> ProbeAsync(
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken);
}

public interface IManifestHealthGate
{
    Task<HealthGateResult> ProbeAsync(
        JsonObject projectManifest,
        JsonObject binding,
        string componentId,
        CancellationToken cancellationToken);
}

public sealed class DeclaredHealthGate(AgentSnapshotCache snapshotCache) : IComponentHealthGate, IManifestHealthGate
{
    public async Task<HealthGateResult> ProbeAsync(
        string projectId,
        string environment,
        string componentId,
        CancellationToken cancellationToken)
    {
        var catalog = snapshotCache.Read().Catalog;
        var manifestEntries = catalog?.Entries.Where(entry =>
            entry.IsValid && entry.ManifestKind == "ProjectManifest" && entry.ProjectId == projectId).ToArray() ?? [];
        var bindingEntries = catalog?.Entries.Where(entry =>
            entry.IsValid && entry.ManifestKind == "EnvironmentBinding" && entry.ProjectId == projectId).ToArray() ?? [];
        if (manifestEntries.Length != 1)
        {
            return new HealthGateResult(false, "无法取得唯一 ProjectManifest");
        }

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestEntries[0].Path, cancellationToken))!.AsObject();
        JsonObject? binding = null;
        foreach (var entry in bindingEntries)
        {
            var candidate = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken))!.AsObject();
            if (candidate["metadata"]?["environment"]?.GetValue<string>() == environment)
            {
                if (binding is not null)
                {
                    return new HealthGateResult(false, "EnvironmentBinding 重复");
                }

                binding = candidate;
            }
        }

        if (binding is null)
        {
            return new HealthGateResult(false, "缺少 EnvironmentBinding");
        }

        return await ProbeAsync(manifest, binding, componentId, cancellationToken);
    }

    public async Task<HealthGateResult> ProbeAsync(
        JsonObject projectManifest,
        JsonObject binding,
        string componentId,
        CancellationToken cancellationToken)
    {
        var component = projectManifest["components"]?.AsArray().OfType<JsonObject>()
            .SingleOrDefault(item => item["id"]?.GetValue<string>() == componentId);
        if (component is null)
        {
            return new HealthGateResult(false, $"ProjectManifest 中不存在组件 {componentId}");
        }

        var probes = component["health"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        if (probes.Length == 0)
        {
            return new HealthGateResult(true, "组件未声明健康探针");
        }

        foreach (var probe in probes)
        {
            var result = await ProbeOneAsync(probe, binding, cancellationToken);
            if (!result.Success)
            {
                return result;
            }
        }

        return new HealthGateResult(true, $"{probes.Length} 个声明式健康探针通过");
    }

    private static async Task<HealthGateResult> ProbeOneAsync(
        JsonObject probe,
        JsonObject binding,
        CancellationToken cancellationToken)
    {
        var kind = probe["kind"]?.GetValue<string>();
        var timeoutSeconds = Math.Clamp(probe["timeoutSeconds"]?.GetValue<double>() ?? 2, 0.2, 10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return kind switch
            {
                "http" => await ProbeHttpAsync(probe, binding, timeout.Token),
                "tcp" => await ProbeTcpAsync(probe, binding, timeout.Token),
                "fileHeartbeat" => ProbeFileHeartbeat(probe, binding),
                _ => new HealthGateResult(false, $"未知健康探针 kind：{kind}")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthGateResult(false, $"{kind} 健康探针超时");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or SocketException or IOException or JsonException)
        {
            return new HealthGateResult(false, $"{kind} 健康探针失败：{exception.Message}");
        }
    }

    private static async Task<HealthGateResult> ProbeHttpAsync(
        JsonObject probe,
        JsonObject binding,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolvePort(probe, binding);
        if (endpoint is null)
        {
            return new HealthGateResult(false, "HTTP portRef 未唯一绑定");
        }

        var path = probe["path"]?.GetValue<string>() ?? "/";
        var uri = new UriBuilder("http", endpoint.Value.Host, endpoint.Value.Port, path).Uri;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var expectedStatus = probe["expectedStatus"]?.GetValue<int>() ?? 200;
        if ((int)response.StatusCode != expectedStatus)
        {
            return new HealthGateResult(false, $"HTTP {(int)response.StatusCode}，期望 {expectedStatus}");
        }

        if (probe["expectJson"] is JsonObject expected)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var pair in expected)
            {
                if (!document.RootElement.TryGetProperty(pair.Key, out var actual) ||
                    actual.GetRawText() != pair.Value!.ToJsonString())
                {
                    return new HealthGateResult(false, $"HTTP JSON 字段 {pair.Key} 不匹配");
                }
            }
        }

        return new HealthGateResult(true, $"HTTP {expectedStatus}");
    }

    private static async Task<HealthGateResult> ProbeTcpAsync(
        JsonObject probe,
        JsonObject binding,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolvePort(probe, binding);
        if (endpoint is null)
        {
            return new HealthGateResult(false, "TCP portRef 未唯一绑定");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Value.Host, endpoint.Value.Port, cancellationToken);
        return new HealthGateResult(true, "TCP 连接成功");
    }

    private static HealthGateResult ProbeFileHeartbeat(JsonObject probe, JsonObject binding)
    {
        var dataRoot = binding["roots"]?["data"]?.GetValue<string>();
        var relativePath = probe["path"]?.GetValue<string>();
        if (dataRoot is null || relativePath is null)
        {
            return new HealthGateResult(false, "文件心跳缺少 data root 或 path");
        }

        var fullPath = Path.GetFullPath(Path.Combine(dataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return new HealthGateResult(false, "文件心跳不存在或路径逃逸");
        }

        var maxAge = TimeSpan.FromSeconds(probe["maxAgeSeconds"]!.GetValue<int>());
        return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(fullPath) <= maxAge
            ? new HealthGateResult(true, "文件心跳新鲜")
            : new HealthGateResult(false, "文件心跳已过期");
    }

    private static (string Host, int Port)? ResolvePort(JsonObject probe, JsonObject binding)
    {
        var portRef = probe["portRef"]?.GetValue<string>();
        var matches = binding["portBindings"]?.AsArray().OfType<JsonObject>()
            .Where(item => item["portId"]?.GetValue<string>() == portRef)
            .ToArray() ?? [];
        if (matches.Length != 1)
        {
            return null;
        }

        var address = matches[0]["address"]!.GetValue<string>();
        var host = address switch
        {
            "0.0.0.0" => "127.0.0.1",
            "::" => "::1",
            _ => address
        };
        return (host, matches[0]["port"]!.GetValue<int>());
    }
}
