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
        Assert.True(secondPlan.AlreadyOnboarded);
        Assert.Equal("已接入项目的声明同步预检通过。", secondPlan.Detail);
        var secondApply = await fixture.Service.ExecuteAsync(
            request with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = secondPlan.PlanToken
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Succeeded, secondApply.Outcome);
        Assert.True(secondApply.AlreadyOnboarded);
        Assert.Equal("项目声明同步完成，当前绑定和声明式健康探针全部通过。", secondApply.Detail);
        Assert.Contains("ProjectManifest 已与项目目录同步", secondApply.Steps);
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

        var preservedPlan = await fixture.Service.ExecuteAsync(
            initialRequest with { Ports = null },
            TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Succeeded, preservedPlan.Outcome);
        Assert.True(preservedPlan.AlreadyOnboarded);
        Assert.Equal(8080, Assert.Single(preservedPlan.Ports).Port);
        Assert.True(preservedPlan.CanApply);

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
    public async Task PlanAndApply_CanAddComponentWithoutChangingExistingBinding()
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
                    [new InventoryItem("OnboardingFixture", "API", "Running", new Dictionary<string, string?>
                    {
                        ["binaryPath"] = Path.Combine(projectRoot, "service.exe")
                    })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));
        var initialRequest = new ExistingProjectOnboardingRequest(
            projectRoot,
            "production",
            ExistingProjectOnboardingAction.Plan);
        var initialPlan = await fixture.Service.ExecuteAsync(initialRequest, TestContext.Current.CancellationToken);
        var initialApply = await fixture.Service.ExecuteAsync(
            initialRequest with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = initialPlan.PlanToken
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Succeeded, initialApply.Outcome);

        await WriteProjectManifestWithWorkerAsync(projectRoot);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [
                        new InventoryItem("OnboardingFixture", "API", "Running", new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(projectRoot, "service.exe")
                        }),
                        new InventoryItem("OnboardingFixture.Worker", "Worker", "Running", new Dictionary<string, string?>
                        {
                            ["binaryPath"] = Path.Combine(projectRoot, "worker.exe")
                        })
                    ])]),
            fixture.Cache.Read().Catalog ?? throw new InvalidOperationException("测试接入后缺少目录快照"));
        var refreshRequest = initialRequest with
        {
            NativeNames = new Dictionary<string, string>
            {
                ["api"] = "OnboardingFixture",
                ["worker"] = "OnboardingFixture.Worker"
            }
        };
        var refreshPlan = await fixture.Service.ExecuteAsync(
            refreshRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, refreshPlan.Outcome);
        Assert.True(refreshPlan.CanApply);
        var refreshApply = await fixture.Service.ExecuteAsync(
            refreshRequest with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = refreshPlan.PlanToken
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, refreshApply.Outcome);
        Assert.True(refreshApply.AlreadyOnboarded);
        var bindingPath = Assert.Single(Directory.EnumerateFiles(fixture.ManifestRoot, "*.binding.json"));
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(
            bindingPath,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, binding.RootElement.GetProperty("metadata").GetProperty("revision").GetInt32());
        Assert.Equal(2, binding.RootElement.GetProperty("componentBindings").GetArrayLength());
    }

    [Fact]
    public async Task PlanAndApply_AllowsUndeployedInteractiveComponentForFirstInstall()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WriteProjectManifestWithInteractiveAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot(
                "TEST-HOST",
                DateTimeOffset.UtcNow,
                [new InventorySection(
                    "windows-services",
                    InventorySourceStatus.Available,
                    [new InventoryItem("OnboardingFixture", "API", "Running", new Dictionary<string, string?>
                    {
                        ["binaryPath"] = Path.Combine(projectRoot, "service.exe")
                    })])]),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));
        var request = new ExistingProjectOnboardingRequest(
            projectRoot,
            "production",
            ExistingProjectOnboardingAction.Plan,
            InteractiveOwnerSid: "S-1-5-21-1234");

        var plan = await fixture.Service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, plan.Outcome);
        Assert.True(plan.CanApply);
        Assert.Equal(2, plan.Components.Count);
        Assert.False(plan.Components.Single(item => item.ComponentId == "host").RequiresInput);

        var applied = await fixture.Service.ExecuteAsync(
            request with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = plan.PlanToken
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, applied.Outcome);
        var bindingPath = Assert.Single(Directory.EnumerateFiles(fixture.ManifestRoot, "*.binding.json"));
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(
            bindingPath,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, binding.RootElement.GetProperty("componentBindings").GetArrayLength());
        Assert.Equal(
            "S-1-5-21-1234",
            binding.RootElement.GetProperty("interactiveSession").GetProperty("ownerSid").GetString());
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

    [Fact]
    public async Task PlanAndApply_Pm2OwnerDiscovery_RequiresExactOwnerWideMatch()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "frontend"));
        await WritePm2ProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        const string ownerSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
        const string pipeName = "CompanyOps.Pm2Bridge.TestOwner.v1";
        Directory.CreateDirectory(fixture.Pm2SnapshotRoot);
        var snapshot = new Pm2Snapshot(
            Pm2SnapshotProtocol.Version,
            ownerSid,
            DateTimeOffset.UtcNow,
            0,
            [
                new Pm2ProcessSnapshot(
                    "integritylink-backend",
                    4,
                    Path.Combine(projectRoot, "backend"),
                    Path.Combine(projectRoot, "backend", ".venv", "Scripts", "python.exe"),
                    "online",
                    1004,
                    0),
                new Pm2ProcessSnapshot(
                    "integritylink-frontend",
                    5,
                    Path.Combine(projectRoot, "frontend"),
                    Path.Combine(projectRoot, "frontend", "node_modules", "vite", "bin", "vite.js"),
                    "online",
                    1005,
                    1)
            ],
            pipeName);
        var discoveryFileName = Pm2SnapshotProtocol.DiscoveryFileName(ownerSid);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Pm2SnapshotRoot, discoveryFileName),
            JsonSerializer.Serialize(snapshot, AgentProtocol.CreateJsonSerializerOptions()),
            TestContext.Current.CancellationToken);

        var request = new ExistingProjectOnboardingRequest(
            projectRoot,
            "production",
            ExistingProjectOnboardingAction.Plan);
        var plan = await fixture.Service.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, plan.Outcome);
        Assert.True(plan.CanApply);
        Assert.Equal([4, 5], plan.Components.Select(static component => component.PmId));
        Assert.All(plan.Components, component =>
        {
            Assert.False(component.RequiresInput);
            Assert.Equal(ownerSid, component.OwnerSid);
            Assert.Equal("PM2 name/cwd/script 唯一精确匹配", component.MatchDetail);
        });
        Assert.Contains(
            plan.Steps,
            step => step.Contains("未读取或复制", StringComparison.Ordinal));

        var applied = await fixture.Service.ExecuteAsync(
            request with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                ExpectedPlanToken = plan.PlanToken
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Succeeded, applied.Outcome);
        var bindingPath = Assert.Single(Directory.EnumerateFiles(fixture.ManifestRoot, "*.binding.json"));
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(
            bindingPath,
            TestContext.Current.CancellationToken));
        var legacyPm2 = binding.RootElement.GetProperty("legacyPm2");
        Assert.Equal(ownerSid, legacyPm2.GetProperty("ownerSid").GetString());
        Assert.Equal(discoveryFileName, legacyPm2.GetProperty("snapshotFileName").GetString());
        Assert.Equal(pipeName, legacyPm2.GetProperty("controlPipeName").GetString());
        Assert.Empty(binding.RootElement.GetProperty("settings").EnumerateArray());
    }

    [Fact]
    public async Task Plan_Pm2NameMatchWithWrongCwd_FailsClosed()
    {
        using var directory = new TestDirectory();
        var projectRoot = Path.Combine(directory.FullPath, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ops"));
        await WritePm2ProjectManifestAsync(projectRoot);
        await WriteOpsReadmeAsync(projectRoot);
        var fixture = await CreateFixtureAsync(directory.FullPath);
        fixture.Cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            new ManifestCatalogSnapshot(DateTimeOffset.UtcNow, []));

        const string ownerSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
        Directory.CreateDirectory(fixture.Pm2SnapshotRoot);
        var snapshot = new Pm2Snapshot(
            Pm2SnapshotProtocol.Version,
            ownerSid,
            DateTimeOffset.UtcNow,
            0,
            [
                new Pm2ProcessSnapshot(
                    "integritylink-backend",
                    4,
                    Path.Combine(directory.FullPath, "different-project", "backend"),
                    Path.Combine(directory.FullPath, "different-project", "backend", "python.exe"),
                    "online",
                    1004,
                    0),
                new Pm2ProcessSnapshot(
                    "integritylink-frontend",
                    5,
                    Path.Combine(projectRoot, "frontend"),
                    Path.Combine(projectRoot, "frontend", "node_modules", "vite", "bin", "vite.js"),
                    "online",
                    1005,
                    0)
            ],
            "CompanyOps.Pm2Bridge.TestOwner.v1");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Pm2SnapshotRoot, Pm2SnapshotProtocol.DiscoveryFileName(ownerSid)),
            JsonSerializer.Serialize(snapshot, AgentProtocol.CreateJsonSerializerOptions()),
            TestContext.Current.CancellationToken);

        var plan = await fixture.Service.ExecuteAsync(
            new ExistingProjectOnboardingRequest(
                projectRoot,
                "production",
                ExistingProjectOnboardingAction.Plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationOutcome.Rejected, plan.Outcome);
        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.Problems,
            problem => problem.Contains("cwd 或 script", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(fixture.ManifestRoot));
    }

    private static async Task<Fixture> CreateFixtureAsync(string root)
    {
        var manifestRoot = Path.Combine(root, "manifests");
        var stateRoot = Path.Combine(root, "state");
        var pm2SnapshotRoot = Path.Combine(stateRoot, "pm2-snapshots");
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
        return new Fixture(service, cache, manifestRoot, pm2SnapshotRoot);
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

    private static Task WriteProjectManifestWithWorkerAsync(string projectRoot) =>
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
                  "health": [{ "kind": "fileHeartbeat", "path": "health/api.json", "maxAgeSeconds": 60 }],
                  "service": { "startMode": "automatic" }
                },
                {
                  "id": "worker",
                  "displayName": "Onboarding Worker",
                  "kind": "windowsService",
                  "entrypoint": "worker-main",
                  "dependsOn": ["api"],
                  "health": [{ "kind": "fileHeartbeat", "path": "health/worker.json", "maxAgeSeconds": 60 }],
                  "service": { "startMode": "automatic" }
                }
              ],
              "ports": [],
              "configuration": [],
              "dataDirectories": [],
              "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 60 }
            }
            """,
            TestContext.Current.CancellationToken);

    private static Task WriteProjectManifestWithInteractiveAsync(string projectRoot) =>
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
                  "health": [{ "kind": "fileHeartbeat", "path": "health/api.json", "maxAgeSeconds": 60 }],
                  "service": { "startMode": "automatic" }
                },
                {
                  "id": "host",
                  "displayName": "Future Host",
                  "kind": "interactiveApp",
                  "entrypoint": "host-main",
                  "dependsOn": ["api"],
                  "health": [{ "kind": "interactiveProcess" }],
                  "interactive": {
                    "executable": "Future.Host.exe",
                    "workingDirectory": ".",
                    "arguments": [],
                    "startPolicy": "userLogon",
                    "stopTimeoutSeconds": 10,
                    "allowForceTerminate": true
                  }
                }
              ],
              "ports": [],
              "configuration": [],
              "dataDirectories": [],
              "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 60 }
            }
            """,
            TestContext.Current.CancellationToken);

    private static Task WritePm2ProjectManifestAsync(string projectRoot) =>
        File.WriteAllTextAsync(
            Path.Combine(projectRoot, "ops", "project-manifest.json"),
            """
            {
              "$schema": "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/project-manifest.schema.json",
              "apiVersion": "ops.company/v1",
              "manifestKind": "ProjectManifest",
              "metadata": {
                "id": "integritylink",
                "displayName": "IntegrityLink",
                "owners": ["platform-team"]
              },
              "components": [
                {
                  "id": "backend",
                  "displayName": "IntegrityLink Backend",
                  "kind": "pm2Legacy",
                  "entrypoint": "backend-main",
                  "dependsOn": [],
                  "health": [{ "kind": "http", "portRef": "backend-http", "path": "/health", "expectedStatus": 200, "timeoutSeconds": 2 }],
                  "pm2": { "name": "integritylink-backend", "cwd": "backend", "script": "backend/.venv/Scripts/python.exe" }
                },
                {
                  "id": "frontend",
                  "displayName": "IntegrityLink Frontend",
                  "kind": "pm2Legacy",
                  "entrypoint": "frontend-main",
                  "dependsOn": ["backend"],
                  "health": [{ "kind": "http", "portRef": "frontend-http", "path": "/", "expectedStatus": 200, "timeoutSeconds": 2 }],
                  "pm2": { "name": "integritylink-frontend", "cwd": "frontend", "script": "frontend/node_modules/vite/bin/vite.js" }
                }
              ],
              "ports": [
                { "id": "backend-http", "componentId": "backend", "protocol": "tcp", "allocation": "fixed", "preferredPort": 8100, "exposure": "lan" },
                { "id": "frontend-http", "componentId": "frontend", "protocol": "tcp", "allocation": "fixed", "preferredPort": 5100, "exposure": "lan" }
              ],
              "configuration": [
                { "key": "SECRET_KEY", "type": "secret", "required": true, "description": "现有项目持有的运行密钥" }
              ],
              "dataDirectories": [],
              "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 60 }
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
        string ManifestRoot,
        string Pm2SnapshotRoot);

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
