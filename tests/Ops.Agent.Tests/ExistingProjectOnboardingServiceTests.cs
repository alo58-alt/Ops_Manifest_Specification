using System.Text.Json;
using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Onboarding;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Projects;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class ExistingProjectOnboardingServiceTests
{
    [Fact]
    public async Task PlanAndApply_ImportOnlyManifestAndBinding_WithoutInstalledState()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [new InventoryItem(
                        "OnboardingFixture",
                        "Onboarding Fixture",
                        "Running",
                        new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(projectRoot, "tools", "nssm.exe")
                        })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));
        var request = new ExistingProjectOnboardingRequest(
            projectRoot,
            "production",
            ExistingProjectOnboardingAction.Plan);

        var plan = await fixture.Service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, plan.Outcome);
        Assert.True(plan.CanApply);
        Assert.Equal("OnboardingFixture", Assert.Single(plan.Components).NativeName);
        Assert.NotNull(plan.PlanToken);
        Assert.Empty(Directory.EnumerateFiles(fixture.ManifestRoot));

        var applied = await fixture.Service.ExecuteAsync(
            request with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = plan.PlanToken
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, applied.Outcome);
        Assert.Equal(2, Directory.EnumerateFiles(fixture.ManifestRoot, "*.json").Count());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture.ManifestRoot),
            path => Path.GetFileName(path).Contains("installed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Cache.Read().Projects!.Projects, project =>
            project.ProjectId == "onboarding-fixture" &&
            project.Status == ProjectBindingStatus.Declared);

        var secondPlan = await fixture.Service.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var secondApply = await fixture.Service.ExecuteAsync(
            request with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = secondPlan.PlanToken
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Succeeded, secondApply.Outcome);
        Assert.True(secondApply.AlreadyOnboarded);
        Assert.Equal(2, Directory.EnumerateFiles(fixture.ManifestRoot, "*.json").Count());
    }

    [Fact]
    public async Task Plan_AmbiguousService_FailsClosedWithoutWritingFiles()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [
                        new InventoryItem("OnboardingFixture-A", "A", "Running", new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(projectRoot, "tools", "nssm.exe")
                        }),
                        new InventoryItem("OnboardingFixture-B", "B", "Running", new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(projectRoot, "tools", "nssm.exe")
                        })
                    ])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        var result = await fixture.Service.ExecuteAsync(
            new ExistingProjectOnboardingRequest(
                projectRoot,
                "production",
                ExistingProjectOnboardingAction.Plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.False(result.CanApply);
        Assert.True(Assert.Single(result.Components).RequiresInput);
        Assert.Empty(Directory.EnumerateFiles(fixture.ManifestRoot));
    }

    [Fact]
    public async Task Apply_WithStalePlanToken_IsRejectedWithoutWritingFiles()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [new InventoryItem("OnboardingFixture", "Fixture", "Running", new Dictionary<string, string?>
                    {
                        ["binaryPath"] = Path.Combine(projectRoot, "tools", "nssm.exe")
                    })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        var result = await fixture.Service.ExecuteAsync(
            new ExistingProjectOnboardingRequest(
                projectRoot,
                "production",
                ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken: "STALE"),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("onboarding_plan_changed", result.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.ManifestRoot));
    }

    [Fact]
    public async Task PlanAndApply_CanCorrectPortOfSameExistingBinding()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestWithPortAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [new InventoryItem("OnboardingFixture", "Fixture", "Running", new Dictionary<string, string?>
                    {
                        ["binaryPath"] = Path.Combine(projectRoot, "service.exe")
                    })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        var initialRequest = new ExistingProjectOnboardingRequest(
            projectRoot,
            "production",
            ExistingProjectOnboardingAction.Plan,
            Ports: new Dictionary<string, int> { ["web-http"] = 8080 });
        var initialPlan = await fixture.Service.ExecuteAsync(initialRequest, TestContext.Current.CancellationToken);
        var initialApply = await fixture.Service.ExecuteAsync(
            initialRequest with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = initialPlan.PlanToken
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Succeeded, initialApply.Outcome);

        var correctedRequest = initialRequest with
        {
            Ports = new Dictionary<string, int> { ["web-http"] = 18342 }
        };
        var correctedPlan = await fixture.Service.ExecuteAsync(
            correctedRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, correctedPlan.Outcome);
        Assert.True(correctedPlan.CanApply);
        Assert.NotNull(correctedPlan.PlanToken);

        var correctedApply = await fixture.Service.ExecuteAsync(
            correctedRequest with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = correctedPlan.PlanToken
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, correctedApply.Outcome);
        var bindingPath = Assert.Single(Directory.EnumerateFiles(fixture.ManifestRoot, "*.binding.json"));
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(
            bindingPath,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, binding.RootElement.GetProperty("metadata").GetProperty("revision").GetInt32());
        Assert.Equal(18342, binding.RootElement.GetProperty("portBindings")[0].GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Plan_SameServiceNameOutsideProjectRoot_FailsClosed()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [new InventoryItem(
                        "OnboardingFixture",
                        "Onboarding Fixture",
                        "Running",
                        new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(directory.FullPath, "other-project", "service.exe")
                        })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        var result = await fixture.Service.ExecuteAsync(
            new ExistingProjectOnboardingRequest(
                projectRoot,
                "production",
                ExistingProjectOnboardingAction.Plan,
                NativeNames: new Dictionary<string, string> { ["api"] = "OnboardingFixture" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.True(Assert.Single(result.Components).RequiresInput);
        Assert.Empty(Directory.EnumerateFiles(fixture.ManifestRoot));
    }

    private static async Task<Fixture> CreateFixtureAsync(string root)
    {
        var manifestRoot = Path.Combine(root, "manifests");
        var stateRoot = Path.Combine(root, "state");
        Directory.CreateDirectory(manifestRoot);
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = manifestRoot,
            StateDirectory = stateRoot,
            InventoryIntervalSeconds = 30
        });
        var pathResolver = new OpsPathResolver(options);
        var catalog = new ManifestCatalog(pathResolver);
        var cache = new AgentSnapshotCache();
        var store = new SqliteOpsStateStore(pathResolver, TestDirectory.CreateJsonOptions());
        await store.InitializeAsync(CancellationToken.None);
        var service = new ExistingProjectOnboardingService(
            pathResolver,
            catalog,
            cache,
            new ProjectRegistry(pathResolver),
            new NoopManifestHealthGate(),
            store,
            new OperationGate(),
            TestDirectory.CreateJsonOptions());
        return new Fixture(service, cache, manifestRoot);
    }

    private static async Task WriteProjectManifestAsync(string projectRoot)
    {
        var manifest = new
        {
            schema = "ignored"
        };
        _ = manifest;
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "ops", "project-manifest.json"),
            """
            {
              "$schema": "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/project-manifest.schema.json",
              "apiVersion": "ops.company/v1",
              "manifestKind": "ProjectManifest",
              "metadata": {
                "id": "onboarding-fixture",
                "displayName": "Onboarding Fixture",
                "owners": ["platform-team"]
              },
              "components": [
                {
                  "id": "api",
                  "displayName": "Onboarding Fixture",
                  "kind": "windowsService",
                  "entrypoint": "api-main",
                  "dependsOn": [],
                  "health": [
                    {
                      "kind": "fileHeartbeat",
                      "path": "health/api.json",
                      "maxAgeSeconds": 60
                    }
                  ],
                  "service": { "startMode": "automatic" }
                }
              ],
              "ports": [],
              "configuration": [],
              "dataDirectories": [],
              "update": {
                "strategy": "stopStart",
                "rollbackOnFailure": true,
                "healthTimeoutSeconds": 60
              }
            }
            """,
            TestContext.Current.CancellationToken);
    }

    private static Task WriteProjectManifestWithPortAsync(string projectRoot) =>
        File.WriteAllTextAsync(
            Path.Combine(projectRoot, "ops", "project-manifest.json"),
            """
            {
              "$schema": "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/project-manifest.schema.json",
              "apiVersion": "ops.company/v1",
              "manifestKind": "ProjectManifest",
              "metadata": {
                "id": "onboarding-fixture",
                "displayName": "Onboarding Fixture",
                "owners": ["platform-team"]
              },
              "components": [
                {
                  "id": "api",
                  "displayName": "Onboarding Fixture",
                  "kind": "windowsService",
                  "entrypoint": "api-main",
                  "dependsOn": [],
                  "health": [
                    {
                      "kind": "http",
                      "portRef": "web-http",
                      "path": "/api/health",
                      "expectedStatus": 200,
                      "timeoutSeconds": 2
                    }
                  ],
                  "service": { "startMode": "automatic" }
                }
              ],
              "ports": [
                {
                  "id": "web-http",
                  "componentId": "api",
                  "protocol": "tcp",
                  "allocation": "fixed",
                  "preferredPort": 18342,
                  "exposure": "lan"
                }
              ],
              "configuration": [],
              "dataDirectories": [],
              "update": {
                "strategy": "stopStart",
                "rollbackOnFailure": true,
                "healthTimeoutSeconds": 60
              }
            }
            """,
            TestContext.Current.CancellationToken);

    private static Task WriteOpsReadmeAsync(string projectRoot) =>
        File.WriteAllTextAsync(
            Path.Combine(projectRoot, "ops", "README.md"),
            "# Onboarding Fixture\n\n用于测试 CompanyOps L1 只读接入。",
            TestContext.Current.CancellationToken);

    private sealed record Fixture(
        ExistingProjectOnboardingService Service,
        AgentSnapshotCache Cache,
        string ManifestRoot);

    private sealed class NoopManifestHealthGate : IManifestHealthGate
    {
        public Task<HealthGateResult> ProbeAsync(
            System.Text.Json.Nodes.JsonObject projectManifest,
            System.Text.Json.Nodes.JsonObject binding,
            string componentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HealthGateResult(true, "测试健康通过"));
    }
}
