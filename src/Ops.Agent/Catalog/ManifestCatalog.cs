using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using CompanyOps.Contracts;
using Json.Schema;

namespace CompanyOps.Agent.Catalog;

public sealed class ManifestCatalog(OpsPathResolver pathResolver) : IManifestCatalog
{
    private const long MaximumManifestBytes = 4 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> SchemaCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> SchemaFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProjectManifest"] = "project-manifest.schema.json",
            ["ReleaseManifest"] = "release-manifest.schema.json",
            ["EnvironmentBinding"] = "environment-binding.schema.json",
            ["InstalledState"] = "installed-state.schema.json",
            ["PortRegistry"] = "port-registry.schema.json"
        };

    public async Task<ManifestCatalogSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var paths = pathResolver.Resolve();
        if (!Directory.Exists(paths.ManifestDirectory))
        {
            return new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []);
        }

        var entries = new List<ManifestCatalogEntry>();
        foreach (var filePath in Directory
                     .EnumerateFiles(paths.ManifestDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await InspectFileAsync(filePath, paths.SchemaDirectory, cancellationToken));
        }

        return new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, entries);
    }

    private static async Task<ManifestCatalogEntry> InspectFileAsync(
        string filePath,
        string schemaDirectory,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        string? manifestKind = null;
        string? projectId = null;
        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Length > MaximumManifestBytes)
        {
            errors.Add($"Manifest 超过 {MaximumManifestBytes} 字节限制");
            return CreateEntry(fileInfo, manifestKind, projectId, errors);
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var document = JsonNode.Parse(
                json,
                documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                    MaxDepth = 100
                });

            if (document is not JsonObject root)
            {
                errors.Add("Manifest 根节点必须是 JSON object");
                return CreateEntry(fileInfo, manifestKind, projectId, errors);
            }

            manifestKind = root["manifestKind"]?.GetValue<string>();
            if (manifestKind is null || !SchemaFiles.TryGetValue(manifestKind, out var schemaFile))
            {
                errors.Add($"未知或缺失 manifestKind：{manifestKind ?? "<null>"}");
                return CreateEntry(fileInfo, manifestKind, projectId, errors);
            }

            projectId = manifestKind == "ProjectManifest"
                ? root["metadata"]?["id"]?.GetValue<string>()
                : root["metadata"]?["projectId"]?.GetValue<string>();

            var schemaPath = Path.Combine(schemaDirectory, schemaFile);
            if (!File.Exists(schemaPath))
            {
                errors.Add($"Agent 缺少内置 Schema：{schemaFile}");
                return CreateEntry(fileInfo, manifestKind, projectId, errors);
            }

            var schema = LoadSchema(schemaPath);
            var evaluation = schema.Evaluate(
                System.Text.Json.JsonSerializer.SerializeToElement(document),
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.Flag
                });
            if (!evaluation.IsValid)
            {
                errors.Add($"未通过 {manifestKind} JSON Schema 校验");
                return CreateEntry(fileInfo, manifestKind, projectId, errors);
            }

            errors.AddRange(ManifestSemanticValidator.Validate(manifestKind, document));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Text.Json.JsonException or
            InvalidOperationException)
        {
            errors.Add($"读取或解析失败：{exception.Message}");
        }

        return CreateEntry(fileInfo, manifestKind, projectId, errors);
    }

    internal static JsonSchema LoadSchema(string schemaPath) =>
        SchemaCache.GetOrAdd(
            schemaPath,
            static path => new Lazy<JsonSchema>(
                () => JsonSchema.FromText(File.ReadAllText(path)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static ManifestCatalogEntry CreateEntry(
        FileInfo fileInfo,
        string? manifestKind,
        string? projectId,
        IReadOnlyList<string> errors) =>
        new(
            fileInfo.FullName,
            manifestKind,
            projectId,
            errors.Count == 0,
            fileInfo.LastWriteTimeUtc,
            errors);
}
