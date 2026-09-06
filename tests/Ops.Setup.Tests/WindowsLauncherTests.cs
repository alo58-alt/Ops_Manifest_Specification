using System.Diagnostics;
using System.Text;
using Xunit;

namespace CompanyOps.Setup.Tests;

public sealed class WindowsLauncherTests
{
    [Theory]
    [InlineData("更新CompanyOps.cmd")]
    [InlineData("生成CompanyOps安装包.cmd")]
    public void LauncherBytes_AreAsciiWithWindowsLineEndings(string launcherName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, launcherName));
        Assert.NotEmpty(bytes);
        Assert.True(bytes.All(value => value < 128), "CMD control text must be ASCII; PowerShell owns localized output.");
        Assert.Equal((byte)'\n', bytes[^1]);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '\n')
                Assert.True(index > 0 && bytes[index - 1] == '\r', "CMD files require CRLF checkout line endings.");
        }
    }

    [Theory]
    [InlineData("更新CompanyOps.cmd", "Update-CompanyOps.ps1", 0, 0)]
    [InlineData("更新CompanyOps.cmd", "Update-CompanyOps.ps1", 17, 17)]
    // A failed inert build must stop before the launcher's Explorer convenience action.
    [InlineData("生成CompanyOps安装包.cmd", "Build-CompanyOpsSetup.ps1", 17, 1)]
    public async Task RealCmd_ReachesOnlyTheFixtureScript_AndPreservesFailure(
        string launcherName, string scriptName, int scriptExitCode, int expectedExitCode)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CompanyOps.Launcher.Tests"));
        var fixture = Path.Combine(parent, "升级 space & (entry) ! " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "tools"));
        try
        {
            var launcher = Path.Combine(fixture, launcherName);
            File.Copy(Path.Combine(AppContext.BaseDirectory, launcherName), launcher);
            var marker = "COMPANYOPS-LAUNCHER-REACHED-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(fixture, "tools", scriptName),
                $"Write-Output '{marker}'\r\nexit {scriptExitCode}\r\n", Encoding.ASCII);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = $"/d /s /v:off /c \"\"{launcher}\"\"",
                WorkingDirectory = fixture,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            Assert.True(process.Start());
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.StandardInput.WriteLineAsync();
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var detail = await output + await error;
            Assert.True(process.ExitCode == expectedExitCode, detail);
            Assert.Contains(marker, detail, StringComparison.Ordinal);
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to remove a launcher fixture outside its isolated directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
