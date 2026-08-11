using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CompanyOps.Contracts;
using CompanyOps.Agent.Catalog;
using Json.Schema;

namespace CompanyOps.Agent.Deployment;

public sealed class ArtifactPackageValidator(OpsPathResolver pathResolver)
{
    private const long MaximumArtifactBytes = 4L * 1024 * 1024 * 1024;

    public async Task<ArtifactValidationResult> ValidateAsync(
        string releaseManifestPath,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        JsonObject? root = null;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(releaseManifestPath, cancellationToken)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            errors.Add($"ReleaseManifest 读取失败：{exception.Message}");
        }

        var projectId = root?["metadata"]?["projectId"]?.GetValue<string>() ?? string.Empty;
        var version = root?["metadata"]?["version"]?.GetValue<string>() ?? string.Empty;
        if (root?["manifestKind"]?.GetValue<string>() != "ReleaseManifest" || projectId.Length == 0 || version.Length == 0)
        {
            errors.Add("ReleaseManifest kind、projectId 或 version 无效");
        }

        if (root is not null)
        {
            var schemaPath = Path.Combine(pathResolver.Resolve().SchemaDirectory, "release-manifest.schema.json");
            if (!File.Exists(schemaPath))
            {
                errors.Add("Agent 缺少 ReleaseManifest Schema");
            }
            else
            {
                var schema = ManifestCatalog.LoadSchema(schemaPath);
                var evaluation = schema.Evaluate(
                    System.Text.Json.JsonSerializer.SerializeToElement(root),
                    new EvaluationOptions { OutputFormat = OutputFormat.Flag });
                if (!evaluation.IsValid)
                {
                    errors.Add("ReleaseManifest 未通过 v1 JSON Schema 校验");
                }

                errors.AddRange(ManifestSemanticValidator.Validate("ReleaseManifest", root));
            }
        }

        var artifactRoot = Path.GetFullPath(artifactDirectory);
        var rootPrefix = artifactRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var validated = new List<ValidatedArtifact>();
        foreach (var artifact in root?["artifacts"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = artifact["id"]?.GetValue<string>() ?? string.Empty;
            var fileName = artifact["fileName"]?.GetValue<string>() ?? string.Empty;
            var expectedHash = artifact["sha256"]?.GetValue<string>() ?? string.Empty;
            var expectedSize = artifact["sizeBytes"]?.GetValue<long>() ?? -1;
            if (Path.GetFileName(fileName) != fileName || fileName.Length == 0)
            {
                errors.Add($"制品 {id} 的 fileName 不是安全的单一文件名");
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(artifactRoot, fileName));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                errors.Add($"制品 {id} 不存在或逃逸制品目录");
                continue;
            }

            var info = new FileInfo(fullPath);
            if (expectedSize < 0 || expectedSize > MaximumArtifactBytes || info.Length != expectedSize)
            {
                errors.Add($"制品 {id} 大小不匹配");
                continue;
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                errors.Add($"制品 {id} SHA-256 不匹配");
                continue;
            }

            validated.Add(new ValidatedArtifact(id, fileName, fullPath, info.Length, actualHash));
        }

        if (validated.Count == 0)
        {
            errors.Add("没有可用制品");
        }

        return new ArtifactValidationResult(errors.Count == 0, projectId, version, validated, errors);
    }
}
