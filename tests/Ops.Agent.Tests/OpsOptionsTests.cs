using CompanyOps.Agent;

namespace CompanyOps.Agent.Tests;

public sealed class OpsOptionsTests
{
    [Fact]
    public void MutationsRequireNonRootProjectInstallAllowlist()
    {
        Assert.False(OpsOptions.HasSafeAllowedProjectInstallRoots(new OpsOptions
        {
            EnableMutations = true,
            AllowedProjectInstallRoots = []
        }));
        Assert.False(OpsOptions.HasSafeAllowedProjectInstallRoots(new OpsOptions
        {
            EnableMutations = true,
            AllowedProjectInstallRoots = null!
        }));
        Assert.False(OpsOptions.HasSafeAllowedProjectInstallRoots(new OpsOptions
        {
            EnableMutations = true,
            AllowedProjectInstallRoots = [Path.GetPathRoot(Environment.SystemDirectory)!]
        }));
        Assert.True(OpsOptions.HasSafeAllowedProjectInstallRoots(new OpsOptions
        {
            EnableMutations = true,
            AllowedProjectInstallRoots = [Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "CompanyOpsApps")]
        }));
    }

    [Fact]
    public void ReadOnlyModeDoesNotRequireProjectInstallAllowlist()
    {
        Assert.True(OpsOptions.HasSafeAllowedProjectInstallRoots(new OpsOptions
        {
            EnableMutations = false,
            AllowedProjectInstallRoots = []
        }));
    }
}
