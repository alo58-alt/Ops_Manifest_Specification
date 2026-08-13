using CompanyOps.Agent;
using CompanyOps.Agent.Projects;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class ProjectRegistryTests
{
    [Fact]
    public async Task ExactBindingInstalledStateAndInventory_AreOwned()
    {
        using var directory = new TestDirectory();
        var catalog = await CreateCatalogAsync(directory.FullPath, installedNativeId: "Company.Sample.Api");
        var registry = CreateRegistry(directory.FullPath);
        var inventory = new InventorySnapshot(
            "TEST-HOST",
            DateTimeOffset.UtcNow,
            [new InventorySection(
                "windows-services",
                InventorySourceStatus.Available,
                [new InventoryItem(
                    "Company.Sample.Api",
                    "Sample API",
                    "Running",
                    new Dictionary<string, string?>
                    {
                        ["binaryPath"] = @"C:\CompanyOps\Apps\sample\service.exe"
                    })])]);

        var snapshot = await registry.BuildAsync(catalog, inventory, CancellationToken.None);

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal(ProjectBindingStatus.Installed, project.Status);
        Assert.Equal(ComponentOwnershipStatus.Owned, Assert.Single(project.Components).Ownership);
    }

    [Fact]
    public async Task InstalledNativeIdMismatch_FailsClosedAsConflict()
    {
        using var directory = new TestDirectory();
        var catalog = await CreateCatalogAsync(directory.FullPath, installedNativeId: "Someone.Else.Api");
        var registry = CreateRegistry(directory.FullPath);
        var inventory = new InventorySnapshot(
            "TEST-HOST",
            DateTimeOffset.UtcNow,
            [new InventorySection("windows-services", InventorySourceStatus.Available, [])]);

        var snapshot = await registry.BuildAsync(catalog, inventory, CancellationToken.None);

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal(ProjectBindingStatus.Conflict, project.Status);
        Assert.Equal(ComponentOwnershipStatus.Conflict, Assert.Single(project.Components).Ownership);
        Assert.Contains(project.Problems, static problem => problem.Contains("nativeId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclaredExistingService_UsesBindingRevisionAndRequiresProjectOwnedImagePath()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectPath = Path.Combine(directory.FullPath, "project.json");
        var bindingPath = Path.Combine(directory.FullPath, "binding.json");
        await File.WriteAllTextAsync(projectPath, """
            {
              "manifestKind": "ProjectManifest",
              "metadata": { "id": "sample", "displayName": "Sample" },
              "components": [
                { "id": "api", "displayName": "API", "kind": "windowsService" }
              ]
            }
            """, cancellationToken);
        await File.WriteAllTextAsync(bindingPath, """
            {
              "manifestKind": "EnvironmentBinding",
              "metadata": {
                "projectId": "sample", "environment": "test", "hostId": "TEST-HOST", "revision": 7
              },
              "roots": { "install": "D:\\project\\sample" },
              "componentBindings": [
                { "componentId": "api", "nativeName": "Company.Sample.Api" }
              ]
            }
            """, cancellationToken);
        var catalog = new ManifestCatalogSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ManifestCatalogEntry(projectPath, "ProjectManifest", "sample", true, DateTimeOffset.UtcNow, []),
                new ManifestCatalogEntry(bindingPath, "EnvironmentBinding", "sample", true, DateTimeOffset.UtcNow, [])
            ]);
        var registry = CreateRegistry(directory.FullPath);

        var owned = await registry.BuildAsync(
            catalog,
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [new InventorySection(
                "windows-services",
                InventorySourceStatus.Available,
                [new InventoryItem(
                    "Company.Sample.Api",
                    "API",
                    "Running",
                    new Dictionary<string, string?> { ["binaryPath"] = @"D:\project\sample\tools\nssm.exe" })])]),
            cancellationToken);

        var ownedProject = Assert.Single(owned.Projects);
        Assert.Equal(ProjectBindingStatus.Declared, ownedProject.Status);
        Assert.Equal(7, ownedProject.Generation);
        Assert.Equal("running", Assert.Single(ownedProject.Components).RuntimeState);
        Assert.Equal(ComponentOwnershipStatus.Owned, Assert.Single(ownedProject.Components).Ownership);

        var conflict = await registry.BuildAsync(
            catalog,
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [new InventorySection(
                "windows-services",
                InventorySourceStatus.Available,
                [new InventoryItem(
                    "Company.Sample.Api",
                    "API",
                    "Running",
                    new Dictionary<string, string?> { ["binaryPath"] = @"D:\other\nssm.exe" })])]),
            cancellationToken);

        var conflictProject = Assert.Single(conflict.Projects);
        Assert.Equal(ProjectBindingStatus.Conflict, conflictProject.Status);
        Assert.Equal(ComponentOwnershipStatus.Conflict, Assert.Single(conflictProject.Components).Ownership);
    }

    [Fact]
    public async Task TwoProjectsClaimingSameNativeService_BothFailClosedAsConflict()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var entries = new List<ManifestCatalogEntry>();
        foreach (var projectId in new[] { "sample-a", "sample-b" })
        {
            var declaredInstallRoot = projectId == "sample-a"
                ? @"C:\CompanyOps\Apps\shared"
                : @"C:\CompanyOps\Apps\shared\nested";
            var projectPath = Path.Combine(directory.FullPath, $"{projectId}.project.json");
            await File.WriteAllTextAsync(
                projectPath,
                $$"""
                {
                  "manifestKind": "ProjectManifest",
                  "metadata": { "id": "{{projectId}}", "displayName": "{{projectId}}" },
                  "components": [
                    { "id": "api", "displayName": "API", "kind": "windowsService" }
                  ]
                }
                """,
                cancellationToken);
            entries.Add(new ManifestCatalogEntry(
                projectPath,
                "ProjectManifest",
                projectId,
                true,
                DateTimeOffset.UtcNow,
                []));
            var bindingPath = Path.Combine(directory.FullPath, $"{projectId}.binding.json");
            await File.WriteAllTextAsync(
                bindingPath,
                $$"""
                {
                  "manifestKind": "EnvironmentBinding",
                  "metadata": {
                    "projectId": "{{projectId}}", "environment": "test", "hostId": "TEST-HOST"
                  },
                  "roots": { "install": {{System.Text.Json.JsonSerializer.Serialize(declaredInstallRoot)}} },
                  "componentBindings": [
                    { "componentId": "api", "nativeName": "Company.Shared.Api" }
                  ]
                }
                """,
                cancellationToken);
            entries.Add(new ManifestCatalogEntry(
                bindingPath,
                "EnvironmentBinding",
                projectId,
                true,
                DateTimeOffset.UtcNow,
                []));
        }

        var snapshot = await CreateRegistry(directory.FullPath).BuildAsync(
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, entries),
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            cancellationToken);

        Assert.Equal(2, snapshot.Projects.Count);
        Assert.All(snapshot.Projects, static project =>
        {
            Assert.Equal(ProjectBindingStatus.Conflict, project.Status);
            Assert.Equal(ComponentOwnershipStatus.Conflict, Assert.Single(project.Components).Ownership);
            Assert.Contains(project.Problems, static problem => problem.Contains("多个项目", StringComparison.Ordinal));
            Assert.Contains(project.Problems, static problem => problem.Contains("安装根目录", StringComparison.Ordinal));
        });
    }

    private static ProjectRegistry CreateRegistry(string path) =>
        new(new OpsPathResolver(
            Options.Create(new OpsOptions
            {
                HostId = "TEST-HOST",
                ManifestDirectory = path,
                StateDirectory = path,
                PipeName = "CompanyOps.Agent.Tests",
                InventoryIntervalSeconds = 30
            })));

    private static async Task<ManifestCatalogSnapshot> CreateCatalogAsync(
        string path,
        string installedNativeId)
    {
        var documents = new Dictionary<string, string>
        {
            ["project.json"] = """
                {
                  "manifestKind": "ProjectManifest",
                  "metadata": { "id": "sample", "displayName": "Sample" },
                  "components": [
                    { "id": "api", "displayName": "API", "kind": "windowsService" }
                  ]
                }
                """,
            ["binding.json"] = """
                {
                  "manifestKind": "EnvironmentBinding",
                  "metadata": { "projectId": "sample", "environment": "test", "hostId": "TEST-HOST" },
                  "roots": { "install": "C:\\CompanyOps\\Apps\\sample" },
                  "componentBindings": [
                    { "componentId": "api", "nativeName": "Company.Sample.Api" }
                  ]
                }
                """,
            ["installed.json"] = $$"""
                {
                  "manifestKind": "InstalledState",
                  "metadata": { "projectId": "sample", "environment": "test", "hostId": "TEST-HOST", "generation": 2 },
                  "release": { "version": "1.2.3" },
                  "components": [
                    {
                      "componentId": "api",
                      "kind": "windowsService",
                      "nativeId": "{{installedNativeId}}",
                      "runtimeState": "running",
                      "healthState": "healthy"
                    }
                  ]
                }
                """
        };

        var entries = new List<ManifestCatalogEntry>();
        foreach (var (fileName, json) in documents)
        {
            var filePath = Path.Combine(path, fileName);
            await File.WriteAllTextAsync(filePath, json);
            var kind = fileName switch
            {
                "project.json" => "ProjectManifest",
                "binding.json" => "EnvironmentBinding",
                _ => "InstalledState"
            };
            entries.Add(new ManifestCatalogEntry(
                filePath,
                kind,
                "sample",
                true,
                DateTimeOffset.UtcNow,
                []));
        }

        return new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, entries);
    }
}
