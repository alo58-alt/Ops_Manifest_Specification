using CompanyOps.Agent.Onboarding;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Tests;

public sealed class ProjectDirectoryBrowserTests
{
    [Fact]
    public void Browse_ListsDirectoriesAndRecognizesProjectRoot()
    {
        using var root = new TestDirectory();
        var project = Path.Combine(root.FullPath, "project-a");
        Directory.CreateDirectory(Path.Combine(project, "ops"));
        File.WriteAllText(Path.Combine(project, "ops", "project-manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(project, "child"));
        var browser = new ProjectDirectoryBrowser();

        var parent = browser.Browse(new DirectoryBrowseRequest(root.FullPath));
        var selected = browser.Browse(new DirectoryBrowseRequest(project));

        Assert.Contains(parent.Directories, item => item.Name == "project-a" && item.FullPath == project);
        Assert.True(selected.IsProjectRoot);
        Assert.Equal(root.FullPath, selected.ParentPath);
        Assert.Contains(selected.Directories, item => item.Name == "child");
    }

    [Fact]
    public void Browse_RejectsRelativeAndMissingDirectories()
    {
        var browser = new ProjectDirectoryBrowser();

        Assert.Throws<InvalidDataException>(() =>
            browser.Browse(new DirectoryBrowseRequest(@"relative\path")));
        Assert.Throws<InvalidDataException>(() =>
            browser.Browse(new DirectoryBrowseRequest(@"Z:\CompanyOps.Tests.Missing")));
    }
}
