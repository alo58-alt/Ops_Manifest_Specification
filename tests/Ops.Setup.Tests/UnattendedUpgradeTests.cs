using Xunit;

namespace CompanyOps.Setup.Tests;

public sealed class UnattendedUpgradeTests
{
    private static readonly string Before = new('a', 40);
    private static readonly string After = new('b', 40);
    private static readonly ExistingInstallation Existing = new(@"C:\CompanyOps", @"D:\CompanyOpsData", true, [@"D:\project"]);
    private static string[] Arguments => ["--upgrade-unattended", "--from-revision", Before,
        "--to-revision", After, "--install-root", Existing.InstallRoot, "--data-root", Existing.DataRoot];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitApply_IsForwarded_AndReturnsMachineReadableResult(bool apply)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var called = false;
        var code = UnattendedUpgradeCommand.Run(apply ? [.. Arguments, "--apply"] : Arguments, output, error,
            (request, progress) =>
            {
                Assert.Equal(apply, request.Apply);
                UnattendedUpgradeCommand.ValidateTarget(request, Existing, Before, After);
                progress.Report("preflight complete");
                called = true;
                return new UnattendedUpgradeResult(apply ? "Upgraded" : "Planned", Before, After,
                    Existing.InstallRoot, Existing.DataRoot, Existing.EnableMutations, Existing.AllowedProjectInstallRoots);
            });
        Assert.True(called);
        Assert.Equal(0, code);
        Assert.Contains(apply ? "Upgraded" : "Planned", output.ToString());
        Assert.Contains("preflight complete", error.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("bad-revision")]
    [InlineData("network-root")]
    public void InvalidArguments_NeverReachTheInstaller(string scenario)
    {
        var args = Arguments;
        args = scenario switch
        {
            "missing" => args[..^1],
            "unknown" => [.. args, "--run-script", "unexpected.ps1"],
            "duplicate" => [.. args, "--apply", "--apply"],
            "bad-revision" => [.. args.Take(2), "main", .. args.Skip(3)],
            "network-root" => [.. args.Take(6), @"\\server\share", .. args.Skip(7)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = UnattendedUpgradeCommand.Run(args, output, error, (_, _) => throw new Exception("unexpected invocation"));
        Assert.Equal(2, code);
        Assert.DoesNotContain("unexpected invocation", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("install-root")]
    [InlineData("data-root")]
    [InlineData("installed-revision")]
    [InlineData("package-revision")]
    public void TargetMismatch_RefusesStaleOrWrongHostState(string scenario)
    {
        var request = UnattendedUpgradeCommand.Parse(Arguments);
        var existing = scenario switch
        {
            "missing" => null,
            "install-root" => Existing with { InstallRoot = @"C:\AnotherOps" },
            "data-root" => Existing with { DataRoot = @"D:\AnotherData" },
            _ => Existing
        };
        Assert.Throws<InvalidOperationException>(() => UnattendedUpgradeCommand.ValidateTarget(request, existing,
            scenario == "installed-revision" ? After : Before, scenario == "package-revision" ? Before : After));
    }

    [Fact]
    public void FailedUpgrade_ReturnsFailure_WithoutSuccessResult()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, UnattendedUpgradeCommand.Run([.. Arguments, "--apply"], output, error,
            (_, _) => throw new InvalidOperationException("rollback completed")));
        Assert.Empty(output.ToString());
        Assert.Contains("rollback completed", error.ToString());
    }
}
