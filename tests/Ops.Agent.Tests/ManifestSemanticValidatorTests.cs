using System.Text.Json.Nodes;
using CompanyOps.Agent.Catalog;

namespace CompanyOps.Agent.Tests;

public sealed class ManifestSemanticValidatorTests
{
    [Fact]
    public void ValidProjectManifest_HasNoSemanticErrors()
    {
        var document = ReadExample("valid", "project-manifest.json");

        var errors = ManifestSemanticValidator.Validate("ProjectManifest", document);

        Assert.Empty(errors);
    }

    [Fact]
    public void CyclicProjectManifest_FailsClosed()
    {
        var document = ReadExample("invalid", "project-manifest-cycle.json");

        var errors = ManifestSemanticValidator.Validate("ProjectManifest", document);

        Assert.Contains(errors, static error => error.Contains("循环", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingReleaseArtifact_FailsClosed()
    {
        var document = ReadExample("invalid", "release-manifest-missing-artifact.json");

        var errors = ManifestSemanticValidator.Validate("ReleaseManifest", document);

        Assert.Contains(errors, static error => error.Contains("不存在的制品", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatePortBinding_FailsClosed()
    {
        var document = ReadExample("invalid", "port-registry-duplicate.json");

        var errors = ManifestSemanticValidator.Validate("PortRegistry", document);

        Assert.Contains(errors, static error => error.Contains("冲突", StringComparison.Ordinal));
    }

    private static JsonNode ReadExample(string category, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples", category, fileName);
        return JsonNode.Parse(File.ReadAllText(path))
               ?? throw new InvalidDataException($"示例为空：{path}");
    }
}
