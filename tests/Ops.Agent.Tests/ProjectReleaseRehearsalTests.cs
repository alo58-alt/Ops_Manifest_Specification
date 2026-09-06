using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

// Runs the production transaction and storage code, but NEVER instantiates a host adapter.
public sealed class ProjectReleaseRehearsalTests
{
    [Fact]
    public async Task InstallUpdateFailureRecoveryAndRollback_PreserveState()
    {
        using var temporary = new TestDirectory();
        var inputPath = Environment.GetEnvironmentVariable("COMPANYOPS_RELEASE_REHEARSAL_INPUT");
        var input = string.IsNullOrEmpty(inputPath)
            ? await CreateFixtureAsync(temporary.FullPath)
            : ReadObject(inputPath);
        var root = Path.GetFullPath(input["outputDirectory"]!.GetValue<string>());
        Assert.False(Directory.Exists(root), "演练输出目录必须不存在，拒绝覆盖既有状态");
        Directory.CreateDirectory(root);
        var results = new List<DeploymentResult>();
        var report = new JsonObject
        {
            ["evidenceLevel"] = "isolated-deployment-transaction",
            ["realHostOperations"] = false,
            ["healthProbes"] = "simulated",
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["succeeded"] = false,
            ["input"] = input.DeepClone()
        };
        try
        {
            var sourceProject = input["projectManifestPath"]!.GetValue<string>();
            var candidatePath = input["releaseManifestPath"]!.GetValue<string>();
            var artifactDirectory = Path.GetDirectoryName(candidatePath)!;
            var candidate = ReadObject(candidatePath);
            var project = ReadObject(sourceProject);
            var projectId = project["metadata"]!["id"]!.GetValue<string>();
            var candidateVersion = candidate["metadata"]!["version"]!.GetValue<string>();
            var manifestRoot = Path.Combine(root, "manifests");
            Directory.CreateDirectory(manifestRoot);
            var projectPath = Path.Combine(manifestRoot, "project.json");
            File.Copy(sourceProject, projectPath);
            var installRoot = Path.Combine(root, "install");
            var dataRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var sentinelPath = Path.Combine(dataRoot, "configuration-and-data.sentinel");
            await File.WriteAllTextAsync(sentinelPath, "isolated data must survive every release switch", TestContext.Current.CancellationToken);
            var sentinelHash = HashFile(sentinelPath);
            var baselinePath = input["baselineReleaseManifestPath"]?.GetValue<string>();
            var baselineArtifactDirectory = baselinePath is null ? artifactDirectory : Path.GetDirectoryName(baselinePath)!;
            report["derivedBaselineUsesCandidatePayload"] = baselinePath is null;
            if (baselinePath is null)
            {
                var baseline = (JsonObject)candidate.DeepClone();
                baseline["metadata"]!["version"] = "0.0.0-rehearsal.baseline";
                baseline["metadata"]!["releaseId"] = "rehearsal-baseline";
                baselinePath = Path.Combine(root, "baseline-release-manifest.json");
                await File.WriteAllTextAsync(baselinePath, baseline.ToJsonString(), TestContext.Current.CancellationToken);
            }
            var baselineVersion = ReadObject(baselinePath)["metadata"]!["version"]!.GetValue<string>();
            Assert.NotEqual(candidateVersion, baselineVersion);
            report["projectId"] = projectId;
            report["sourceRevision"] = candidate["metadata"]!["sourceRevision"]?.DeepClone();
            report["candidateVersion"] = candidateVersion;
            report["baselineVersion"] = baselineVersion;
            report["projectManifestSha256"] = HashFile(projectPath);
            report["releaseManifestSha256"] = HashFile(candidatePath);
            report["artifacts"] = candidate["artifacts"]!.DeepClone();

            var components = project["components"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert.All(components, c => Assert.Contains(c["kind"]!.GetValue<string>(), new[] { "windowsService", "interactiveApp" }));
            var binding = new JsonObject
            {
                ["manifestKind"] = "EnvironmentBinding",
                ["metadata"] = new JsonObject { ["projectId"] = projectId, ["environment"] = "rehearsal", ["hostId"] = "REHEARSAL-HOST" },
                ["roots"] = new JsonObject { ["install"] = installRoot, ["source"] = Path.Combine(root, "source"), ["data"] = dataRoot, ["logs"] = Path.Combine(root, "logs") },
                ["componentBindings"] = new JsonArray(components.Select(c => (JsonNode)new JsonObject
                {
                    ["componentId"] = c["id"]!.GetValue<string>(),
                    ["nativeName"] = "Rehearsal." + c["id"]!.GetValue<string>()
                }).ToArray()),
                ["portBindings"] = new JsonArray((project["ports"]?.AsArray().OfType<JsonObject>() ?? []).Select((p, i) => (JsonNode)new JsonObject
                {
                    ["portId"] = p["id"]!.GetValue<string>(), ["componentId"] = p["componentId"]!.GetValue<string>(),
                    ["protocol"] = p["protocol"]!.GetValue<string>(), ["address"] = "127.0.0.1", ["port"] = 41000 + i
                }).ToArray())
            };
            var bindingPath = Path.Combine(manifestRoot, "binding.json");
            await File.WriteAllTextAsync(bindingPath, binding.ToJsonString(), TestContext.Current.CancellationToken);
            var options = Options.Create(new OpsOptions
            {
                HostId = "REHEARSAL-HOST", ManifestDirectory = manifestRoot, StateDirectory = Path.Combine(root, "state"),
                EnableMutations = true, AllowedProjectInstallRoots = [root]
            });
            var resolver = new OpsPathResolver(options);
            var jsonOptions = TestDirectory.CreateJsonOptions();
            var stateStore = new SqliteOpsStateStore(resolver, jsonOptions);
            await stateStore.InitializeAsync(CancellationToken.None);
            var ports = new SqlitePortRegistryStore(resolver);
            await ports.InitializeAsync(CancellationToken.None);
            var host = new SimulatedHost();
            var kinds = components.Select(c => c["kind"]!.GetValue<string>()).Distinct().ToArray();
            var activator = new NativeDeploymentActivator(
                kinds.Select(k => new SimulatedEntrypoints(k, host)),
                kinds.Select(k => new SimulatedControl(k, host)), host);
            var cache = new AgentSnapshotCache();
            var installedStatePath = Path.Combine(manifestRoot, projectId + ".rehearsal.REHEARSAL-HOST.installed-state.json");
            var pointerPath = Path.Combine(installRoot, "current.release.json");
            var engine = new DeploymentEngine(cache, new ArtifactPackageValidator(resolver), new SafeZipExtractor(),
                ports, activator, new OperationGate(), stateStore, resolver, options, jsonOptions);
            long generation = 0;
            void Refresh()
            {
                var state = File.Exists(installedStatePath) ? ReadObject(installedStatePath) : null;
                generation = state?["metadata"]?["generation"]?.GetValue<long>() ?? 0;
                var runtime = new ProjectRuntimeView(projectId, projectId, "rehearsal",
                    state is null ? ProjectBindingStatus.Declared : ProjectBindingStatus.Installed,
                    state?["release"]?["version"]?.GetValue<string>(), generation,
                    components.Select(c => new ProjectComponentRuntimeView(c["id"]!.GetValue<string>(), c["id"]!.GetValue<string>(),
                        c["kind"]!.GetValue<string>(), "Rehearsal." + c["id"]!.GetValue<string>(), "Rehearsal." + c["id"]!.GetValue<string>(),
                        state is null ? ComponentOwnershipStatus.DeclaredOnly : ComponentOwnershipStatus.Owned, "running", "simulated", null)).ToArray(), [])
                    { HasInstalledState = state is not null, InstallRoot = installRoot };
                cache.Update(new InventorySnapshot("REHEARSAL-HOST", DateTimeOffset.UtcNow, []),
                    new ManifestCatalogSnapshot(DateTimeOffset.UtcNow,
                    [new(projectPath, "ProjectManifest", projectId, true, DateTimeOffset.UtcNow, []),
                     new(bindingPath, "EnvironmentBinding", projectId, true, DateTimeOffset.UtcNow, [])]),
                    new ProjectRegistrySnapshot("REHEARSAL-HOST", DateTimeOffset.UtcNow, [runtime]));
            }
            async Task<DeploymentResult> Run(string id, DeploymentAction action, string? path, string? artifacts)
            {
                var result = await engine.ExecuteAsync(new(id, id, projectId, "rehearsal", action, generation, path, artifacts), CancellationToken.None);
                results.Add(result);
                return result;
            }
            static void Succeeded(DeploymentResult result) => Assert.True(result.Outcome == OperationOutcome.Succeeded, result.ErrorCode + ": " + result.Detail);
            Refresh();
            Succeeded(await Run("baseline-plan", DeploymentAction.Plan, baselinePath, baselineArtifactDirectory));
            Assert.Empty(host.Events);
            Succeeded(await Run("baseline-install", DeploymentAction.Install, baselinePath, baselineArtifactDirectory));
            Refresh();
            Assert.Equal(1, generation);
            var baselinePointer = File.ReadAllBytes(pointerPath);
            var baselineState = File.ReadAllBytes(installedStatePath);
            var baselineEntrypoints = host.Entrypoints.ToDictionary();

            var corrupt = (JsonObject)candidate.DeepClone();
            corrupt["artifacts"]![0]!["sha256"] = new string('0', 64);
            var corruptPath = Path.Combine(root, "corrupt-release-manifest.json");
            await File.WriteAllTextAsync(corruptPath, corrupt.ToJsonString(), TestContext.Current.CancellationToken);
            var eventCount = host.Events.Count;
            var corruptResult = await Run("reject-bad-hash", DeploymentAction.Update, corruptPath, artifactDirectory);
            Assert.Equal("artifact_validation_failed", corruptResult.ErrorCode);
            Assert.Equal(eventCount, host.Events.Count);
            Assert.Equal(baselinePointer, File.ReadAllBytes(pointerPath));

            Succeeded(await Run("candidate-plan", DeploymentAction.Plan, candidatePath, artifactDirectory));
            host.FailNextHealthFor = components.Last()["id"]!.GetValue<string>();
            var failure = await Run("candidate-health-failure", DeploymentAction.Update, candidatePath, artifactDirectory);
            Assert.Equal("activation_failed", failure.ErrorCode);
            Assert.Equal(baselinePointer, File.ReadAllBytes(pointerPath));
            Assert.Equal(baselineState, File.ReadAllBytes(installedStatePath));
            Assert.All(baselineEntrypoints, item => Assert.Equal(item.Value, host.Entrypoints[item.Key]));
            Assert.All(components, c => Assert.True(host.Running[c["id"]!.GetValue<string>()]));
            Assert.True(Directory.Exists(Path.Combine(installRoot, ".failed", "candidate-health-failure")));
            foreach (var port in binding["portBindings"]!.AsArray().OfType<JsonObject>())
            {
                var conflict = await ports.ReserveAsync([new(port["protocol"]!.GetValue<string>(), "127.0.0.1",
                    port["port"]!.GetValue<int>(), "another-project", "rehearsal", "other", "other-port", "competitor")], CancellationToken.None);
                Assert.False(conflict.Success, "更新失败后旧版 active 端口登记必须仍阻止其他项目占用");
                Assert.Equal("port_conflict", conflict.ErrorCode);
            }
            Succeeded(await Run("candidate-update", DeploymentAction.Update, candidatePath, artifactDirectory));
            eventCount = host.Events.Count;
            Succeeded(await Run("candidate-update", DeploymentAction.Update, candidatePath, artifactDirectory));
            Assert.Equal(eventCount, host.Events.Count); // Same idempotency key must not restart twice.
            Refresh();
            Assert.Equal(2, generation);
            Assert.Equal(candidateVersion, ReadObject(pointerPath)["currentVersion"]!.GetValue<string>());
            Succeeded(await Run("explicit-rollback", DeploymentAction.Rollback, null, null));
            Refresh();
            Assert.Equal(3, generation);
            Assert.Equal(baselineVersion, ReadObject(pointerPath)["currentVersion"]!.GetValue<string>());
            Assert.All(baselineEntrypoints, item => Assert.Equal(item.Value, host.Entrypoints[item.Key]));
            Assert.Equal(sentinelHash, HashFile(sentinelPath));
            var audits = await stateStore.ReadRecentAuditEventsAsync(30, CancellationToken.None);
            Assert.Equal(7, audits.Count);
            Assert.Contains(audits, a => a.Outcome == "Rejected" && a.Action == "Update");
            report["audit"] = JsonSerializer.SerializeToNode(audits, jsonOptions);
            report["simulatedHostEvents"] = JsonSerializer.SerializeToNode(host.Events);
            report["finalGeneration"] = generation;
            report["dataSentinelSha256"] = sentinelHash;
            report["succeeded"] = true;
        }
        finally
        {
            report["completedAt"] = DateTimeOffset.UtcNow.ToString("O");
            report["operations"] = JsonSerializer.SerializeToNode(results, TestDirectory.CreateJsonOptions());
            await File.WriteAllTextAsync(Path.Combine(root, "rehearsal-result.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
    }

    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<JsonObject> CreateFixtureAsync(string root)
    {
        var projectPath = Path.Combine(root, "project.json");
        await File.WriteAllTextAsync(projectPath, """
            {"manifestKind":"ProjectManifest","metadata":{"id":"sample"},
             "components":[{"id":"api","kind":"windowsService","entrypoint":"api-main","dependsOn":[],"health":[]},
                           {"id":"host","kind":"interactiveApp","entrypoint":"host-main","dependsOn":["api"],"health":[]}],
             "ports":[{"id":"api-http","componentId":"api","protocol":"tcp"}],
             "update":{"strategy":"stopStart","rollbackOnFailure":true,"healthTimeoutSeconds":5}}
            """);
        var zipPath = Path.Combine(root, "package.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "api/app.exe", "host/app.exe" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("inert fixture, never executed");
            }
        }
        var releasePath = Path.Combine(root, "release-manifest.json");
        await File.WriteAllTextAsync(releasePath, $$"""
            {"$schema":"release-manifest.schema.json","apiVersion":"ops.company/v1","manifestKind":"ReleaseManifest",
             "metadata":{"projectId":"sample","version":"1.0.0","releaseId":"sample-1.0.0","builtAt":"2026-09-06T00:00:00Z","sourceRevision":"1111111111111111111111111111111111111111"},
             "target":{"os":"windows","architecture":"x64","minAgentVersion":"0.1.0"},
             "projectManifestSha256":"{{HashFile(projectPath)}}",
             "artifacts":[{"id":"package","fileName":"package.zip","mediaType":"application/zip","sha256":"{{HashFile(zipPath)}}","sizeBytes":{{new FileInfo(zipPath).Length}}}],
             "componentPayloads":[{"componentId":"api","entrypoint":"api-main","artifactId":"package","path":"api/app.exe"},
                                  {"componentId":"host","entrypoint":"host-main","artifactId":"package","path":"host/app.exe"}]}
            """);
        return new JsonObject { ["projectManifestPath"] = projectPath, ["releaseManifestPath"] = releasePath, ["outputDirectory"] = Path.Combine(root, "rehearsal") };
    }

    private sealed class SimulatedHost : IManifestHealthGate
    {
        public Dictionary<string, string> Entrypoints { get; } = new();
        public Dictionary<string, bool> Running { get; } = new();
        public List<string> Events { get; } = new();
        public string? FailNextHealthFor { get; set; }
        public Task<HealthGateResult> ProbeAsync(JsonObject projectManifest, JsonObject binding, string componentId, CancellationToken cancellationToken)
        {
            Events.Add("health:" + componentId);
            var fail = FailNextHealthFor == componentId;
            // Throw once to exercise the real activation exception/recovery path without waiting
            // a project's production health timeout (which can be several minutes).
            if (fail)
            {
                FailNextHealthFor = null;
                throw new IOException("injected health probe failure");
            }
            return Task.FromResult(new HealthGateResult(!fail, fail ? "injected health failure" : "simulated healthy"));
        }
    }

    private sealed class SimulatedEntrypoints(string kind, SimulatedHost host) : IDeploymentEntrypointAdapter
    {
        public string Kind => kind;
        public Task<DeploymentEntrypointCaptureResult> CaptureAsync(DeploymentEntrypointTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new DeploymentEntrypointCaptureResult(true, new DeploymentEntrypointSnapshot(target.ComponentId, target.Kind, target.NativeName,
                host.Entrypoints.GetValueOrDefault(target.ComponentId, "inert-original.exe"), host.Running.GetValueOrDefault(target.ComponentId))));
        public Task<AdapterExecutionResult> ApplyAsync(DeploymentEntrypointTarget target, DeploymentEntrypointSnapshot snapshot, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(target.ExecutablePath), "真实发布载荷入口必须存在");
            host.Entrypoints[target.ComponentId] = target.BinaryPath;
            host.Events.Add("apply:" + target.ComponentId);
            return Task.FromResult(new AdapterExecutionResult(true, "simulated entrypoint"));
        }
        public Task<AdapterExecutionResult> RestoreAsync(DeploymentEntrypointSnapshot snapshot, CancellationToken cancellationToken)
        {
            host.Entrypoints[snapshot.ComponentId] = snapshot.BinaryPath;
            host.Events.Add("restore:" + snapshot.ComponentId);
            return Task.FromResult(new AdapterExecutionResult(true, "simulated restore"));
        }
    }

    private sealed class SimulatedControl(string kind, SimulatedHost host) : IComponentControlAdapter
    {
        public string Kind => kind;
        public Task<AdapterExecutionResult> ExecuteAsync(ComponentControlTarget target, ComponentOperationAction action, CancellationToken cancellationToken)
        {
            host.Running[target.ComponentId] = action != ComponentOperationAction.Stop;
            host.Events.Add(action + ":" + target.ComponentId);
            return Task.FromResult(new AdapterExecutionResult(true, "simulated control; no process launched"));
        }
    }
}
