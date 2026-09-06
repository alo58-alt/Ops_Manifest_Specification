using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Updates;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class ProjectReleaseBuildRunnerTests
{
    [Theory]
    [InlineData(936)]
    [InlineData(1252)]
    [InlineData(65001)]
    public async Task PackagedHelper_DecodesAndParsesInWindowsPowerShell_RegardlessOfAnsiCodePage(int codePage)
    {
        // Exercise the Windows PowerShell parser with its BOM-aware file loading semantics.
        // An explicit ANSI fallback prevents a UTF-8 system locale from hiding missing BOMs.
        const string probe = """
            $ErrorActionPreference = 'Stop'
            $path = $env:COMPANYOPS_TEST_SCRIPT_PATH
            $fallback = [Text.Encoding]::GetEncoding([int]$env:COMPANYOPS_TEST_ANSI_CODE_PAGE)
            $reader = [IO.StreamReader]::new($path, $fallback, $true)
            try { $scriptText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $utf8Text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
            if ($scriptText -cne $utf8Text) { throw 'Script decoding differs from UTF-8. Save the script as UTF-8 with BOM.' }
            $tokens = $null
            $parseErrors = $null
            $null = [Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
            Write-Output 'WINDOWS-POWERSHELL-SCRIPT-DECODE-AND-PARSE-PASSED'
            """;
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(probe)) })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["COMPANYOPS_TEST_SCRIPT_PATH"] = Path.Combine(
            AppContext.BaseDirectory, "tools", "New-ProjectRelease.ps1");
        startInfo.Environment["COMPANYOPS_TEST_ANSI_CODE_PAGE"] = codePage.ToString();
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var detail = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, detail);
        Assert.Contains("WINDOWS-POWERSHELL-SCRIPT-DECODE-AND-PARSE-PASSED", detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DefaultWindowsPowerShell_BuildsAndValidatesUsingPackagedAgent(int argumentCount)
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.FullPath, "源码 发布");
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
            # 中文构建入口需要 UTF-8 BOM，兼容 Windows PowerShell 5.1。
            $ErrorActionPreference = 'Stop'
            $root = Split-Path -Parent $PSScriptRoot
            & (Join-Path $OpsSpecificationRoot 'tools\New-ProjectRelease.ps1') `
                -ProjectManifestPath (Join-Path $root 'project.json') `
                -RecipePath (Join-Path $root 'recipe.json') `
                -PayloadDirectory (Join-Path $root 'payload') `
                -OutputDirectory $OutputDirectory -Version $Version -ReleaseId $ReleaseId `
                -SourceRevision $env:COMPANYOPS_EXPECTED_SOURCE_REVISION
            exit $LASTEXITCODE
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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
