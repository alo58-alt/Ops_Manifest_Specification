using System.Diagnostics;
using System.Text;
using Xunit;

namespace CompanyOps.Setup.Tests;

public sealed class PrebuiltUpdateTests
{
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task RealPublisherAndReader_RejectCorruptionWithoutStartingAnInstaller(string shell)
    {
        var path = shell == "powershell.exe"
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", shell) : shell;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(AppContext.BaseDirectory, "Test-CompanyOpsPrebuiltUpdate.ps1"),
                     "-UpdaterPath", Path.Combine(AppContext.BaseDirectory, "tools", "Update-CompanyOps.ps1"),
                     "-PublisherPath", Path.Combine(AppContext.BaseDirectory, "tools", "Publish-CompanyOpsPackage.ps1"),
                     "-AssemblyPath", typeof(InstallerEngine).Assembly.Location })
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var detail = await output + await error;
        Assert.True(process.ExitCode == 0, detail);
        Assert.Contains("COMPANYOPS-PREBUILT-TESTS-PASSED", detail);
    }
}
