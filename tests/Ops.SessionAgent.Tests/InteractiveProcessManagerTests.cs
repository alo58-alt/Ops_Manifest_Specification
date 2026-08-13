using Xunit;

namespace CompanyOps.SessionAgent.Tests;

public sealed class InteractiveProcessManagerTests
{
    [Fact]
    public void SelectUniqueCandidate_AdoptsExactExecutableInBoundSession()
    {
        var candidates = new[]
        {
            new InteractiveProcessCandidate(100, 50, @"D:\apps\host.exe", 1),
            new InteractiveProcessCandidate(200, 50, @"D:\apps\host.exe", 2),
            new InteractiveProcessCandidate(300, 50, @"D:\other\host.exe", 1)
        };

        var selected = InteractiveProcessManager.SelectUniqueCandidate(
            candidates,
            @"D:\apps\host.exe",
            1);

        Assert.Equal(100, selected);
    }

    [Fact]
    public void SelectUniqueCandidate_AdoptsSingleParentChildTree()
    {
        var candidates = new[]
        {
            new InteractiveProcessCandidate(100, 50, @"D:\apps\host.exe", 1),
            new InteractiveProcessCandidate(101, 100, @"D:\apps\HOST.exe", 1)
        };

        var selected = InteractiveProcessManager.SelectUniqueCandidate(
            candidates,
            @"D:\apps\host.exe",
            1);

        Assert.Equal(100, selected);
    }

    [Fact]
    public void SelectUniqueCandidate_FailsClosedForTwoIndependentTrees()
    {
        var candidates = new[]
        {
            new InteractiveProcessCandidate(100, 50, @"D:\apps\host.exe", 1),
            new InteractiveProcessCandidate(101, 60, @"D:\apps\HOST.exe", 1)
        };

        var selected = InteractiveProcessManager.SelectUniqueCandidate(
            candidates,
            @"D:\apps\host.exe",
            1);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectUniqueCandidate_RejectsSameExecutableFromAnotherSession()
    {
        var selected = InteractiveProcessManager.SelectUniqueCandidate(
            [new InteractiveProcessCandidate(100, 50, @"D:\apps\host.exe", 2)],
            @"D:\apps\host.exe",
            1);

        Assert.Null(selected);
    }
}
