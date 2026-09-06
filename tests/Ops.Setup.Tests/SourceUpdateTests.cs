using System.Diagnostics;
using System.Text;
using Xunit;

namespace CompanyOps.Setup.Tests;

public sealed class SourceUpdateTests
{
    [Fact]
    public async Task SourceUpdater_IsolatedOrchestrationAndBoundedProcessChecksPass()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "Test-CompanyOpsUpdate.ps1"),
                     "-UpdaterPath", Path.Combine(AppContext.BaseDirectory, "Update-CompanyOpsFromSource.ps1") })
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var detail = await output + await error;
        Assert.True(process.ExitCode == 0, detail);
        Assert.Contains("COMPANYOPS-SOURCE-UPDATE-TESTS-PASSED:19", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeOnly_RefusesFirstInstallation()
    {
        Assert.Throws<InvalidOperationException>(() => InstallerEngine.RequireExistingInstallation(null, true));
        InstallerEngine.RequireExistingInstallation(null, false);
        InstallerEngine.RequireExistingInstallation(new ExistingInstallation(@"C:\CompanyOps", @"C:\CompanyOpsData", false, []), true);
    }

    [Fact]
    public void SetupLease_RejectsConcurrentThread_ThenCanBeReacquired()
    {
        var name = @"Local\CompanyOps.Setup.Tests." + Guid.NewGuid().ToString("N");
        using (PlatformSetupLease.Acquire(name))
        {
            Exception? failure = null;
            var contender = new Thread(() =>
            {
                try { using var unexpected = PlatformSetupLease.Acquire(name); }
                catch (Exception exception) { failure = exception; }
            });
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
            Assert.IsType<InvalidOperationException>(failure);
        }
        using var resumed = PlatformSetupLease.Acquire(name);
    }
}
