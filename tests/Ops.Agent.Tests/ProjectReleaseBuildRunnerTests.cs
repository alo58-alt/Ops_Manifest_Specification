using System.Text.Json.Nodes;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Updates;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class ProjectReleaseBuildRunnerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DefaultWindowsPowerShell_BuildsAndValidatesUsingPackagedAgent(int argumentCount)
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.FullPath, "source");
        var sourceTools = Path.Combine(source, "tools");
        var payload = Path.Combine(source, "payload", "api");
        Directory.CreateDirectory(sourceTools);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "app.exe"), "inert fixture, never executed");
        File.WriteAllText(Path.Combine(source, "project.json"), """
            {"apiVersion":"ops.company/v1","manifestKind":"ProjectManifest",
             "metadata":{"id":"sample"},"components":[{"id":"api","entrypoint":"api-main"}]}
            """);
        File.WriteAllText(Path.Combine(source, "recipe.json"), """
            {"apiVersion":"ops.company/v1","recipeKind":"ReleaseRecipe",
             "artifact":{"id":"package","fileName":"sample-${VERSION}.zip"},
             "target":{"architecture":"x64","minAgentVersion":"0.1.0"},
             "componentPayloads":[{"componentId":"api","entrypoint":"api-main","path":"api/app.exe"}]}
            """);
        if (argumentCount > 0)
        {
            var recipePath = Path.Combine(source, "recipe.json");
            var recipe = JsonNode.Parse(File.ReadAllText(recipePath))!;
            recipe["componentPayloads"]![0]!["arguments"] = new JsonArray(
                Enumerable.Range(0, argumentCount).Select(i => (JsonNode?)JsonValue.Create("arg" + i)).ToArray());
            File.WriteAllText(recipePath, recipe.ToJsonString());
        }
        File.WriteAllText(Path.Combine(sourceTools, "Build-CompanyOpsRelease.ps1"), """
            param($Version, $ReleaseId, $OpsSpecificationRoot, $OutputDirectory)
            $ErrorActionPreference = 'Stop'
            $root = Split-Path -Parent $PSScriptRoot
            & (Join-Path $OpsSpecificationRoot 'tools\New-ProjectRelease.ps1') `
                -ProjectManifestPath (Join-Path $root 'project.json') `
                -RecipePath (Join-Path $root 'recipe.json') `
                -PayloadDirectory (Join-Path $root 'payload') `
                -OutputDirectory $OutputDirectory -Version $Version -ReleaseId $ReleaseId `
                -SourceRevision $env:COMPANYOPS_EXPECTED_SOURCE_REVISION
            exit $LASTEXITCODE
            """);
        var stateRoot = Path.Combine(directory.FullPath, "state");
        var options = Options.Create(new OpsOptions { StateDirectory = stateRoot, GitBuildTimeoutMinutes = 1 });
        var runner = new ProjectReleaseBuildRunner(new OpsPathResolver(options), options);
        const string revision = "1111111111111111111111111111111111111111";

        var result = await runner.BuildAsync(source, "sample", "1.2.3", "sample-1.2.3", revision,
            "packaged-build", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        Assert.Contains("Release validated: sample 1.2.3", result.Detail);
        var manifest = JsonNode.Parse(File.ReadAllText(result.ReleaseManifestPath!))!;
        Assert.Equal(revision, manifest["metadata"]!["sourceRevision"]!.GetValue<string>());
        Assert.Equal(argumentCount, manifest["componentPayloads"]![0]!["arguments"]!.AsArray().Count);
        Assert.True(File.Exists(Path.Combine(result.ArtifactDirectory!, "sample-1.2.3.zip")));
        Assert.Empty(Directory.GetFiles(stateRoot, "*.db", SearchOption.AllDirectories));
    }
}
