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
    public void DuplicateReleaseComponentPayload_FailsClosed()
    {
        var document = ReadExample("invalid", "release-manifest-duplicate-component.json");

        var errors = ManifestSemanticValidator.Validate("ReleaseManifest", document);

        Assert.Contains(errors, static error => error.Contains("componentId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("release-manifest-pm2-identity-mismatch.json", "完全一致")]
    [InlineData("release-manifest-pm2-secret.json", "Secret 明文")]
    [InlineData("release-manifest-pm2-shell.json", "命令宿主")]
    public void UnsafePm2ReleasePayload_FailsClosed(string fileName, string expectedError)
    {
        var document = ReadExample("invalid", fileName);

        var errors = ManifestSemanticValidator.Validate("ReleaseManifest", document);

        Assert.Contains(errors, error => error.Contains(expectedError, StringComparison.Ordinal));
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
