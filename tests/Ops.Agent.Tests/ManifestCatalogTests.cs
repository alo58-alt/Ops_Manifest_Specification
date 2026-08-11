using CompanyOps.Agent.Catalog;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class ManifestCatalogTests
{
    [Fact]
    public async Task InspectAsync_UsesEmbeddedSchemaAndReportsValidManifest()
    {
        using var testDirectory = new TestDirectory();
        var manifestDirectory = Path.Combine(testDirectory.FullPath, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "examples",
                "valid",
                "project-manifest.json"),
            Path.Combine(manifestDirectory, "sample.project.json"));

        var catalog = CreateCatalog(testDirectory.FullPath, manifestDirectory);

        var snapshot = await catalog.InspectAsync(CancellationToken.None);

        var entry = Assert.Single(snapshot.Entries);
        Assert.True(entry.IsValid);
        Assert.Equal("ProjectManifest", entry.ManifestKind);
        Assert.Equal("sample-system", entry.ProjectId);
        Assert.Empty(entry.Errors);
    }

    [Fact]
    public async Task InspectAsync_RejectsSemanticCycle()
    {
        using var testDirectory = new TestDirectory();
        var manifestDirectory = Path.Combine(testDirectory.FullPath, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "examples",
                "invalid",
                "project-manifest-cycle.json"),
            Path.Combine(manifestDirectory, "cyclic.project.json"));

        var catalog = CreateCatalog(testDirectory.FullPath, manifestDirectory);

        var snapshot = await catalog.InspectAsync(CancellationToken.None);

        var entry = Assert.Single(snapshot.Entries);
        Assert.False(entry.IsValid);
        Assert.Contains(entry.Errors, static error => error.Contains("循环", StringComparison.Ordinal));
    }

    private static ManifestCatalog CreateCatalog(
        string stateDirectory,
        string manifestDirectory)
    {
        var options = Options.Create(
            new OpsOptions
            {
                HostId = "TEST-HOST",
                ManifestDirectory = manifestDirectory,
                StateDirectory = stateDirectory
            });
        return new ManifestCatalog(new OpsPathResolver(options));
    }
}
