using System.IO.Compression;
using System.Security.Cryptography;
using CompanyOps.Agent;
using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class DeploymentEngineTransactionTests
{
    [Fact]
    public async Task ProjectManifestHashMismatch_IsRejectedBeforeActivationPlan()
    {
        using var directory = new TestDirectory();
        var setup = await CreateSetupAsync(
            directory.FullPath,
            useCorrectProjectHash: false,
            failPortCommit: false,
            allowInstallRoot: true);

        var result = await setup.Engine.ExecuteAsync(setup.Request, CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("project_manifest_hash_mismatch", result.ErrorCode);
        Assert.Equal(0, setup.Activator.PlanCalls);
        Assert.False(Directory.Exists(Path.Combine(setup.InstallRoot, "releases", "1.0.0")));

        var auditEvents = await setup.StateStore.ReadRecentAuditEventsAsync(10, CancellationToken.None);
        var auditEvent = Assert.Single(auditEvents);
        Assert.Equal("deployment", auditEvent.Category);
        Assert.Equal("Install", auditEvent.Action);
        Assert.Equal("Rejected", auditEvent.Outcome);
        Assert.Contains("project_manifest_hash_mismatch", auditEvent.Detail);
    }

    [Fact]
    public async Task PortCommitFailure_RestoresNativeEntrypointAndStateFiles()
    {
        using var directory = new TestDirectory();
        var setup = await CreateSetupAsync(
            directory.FullPath,
            useCorrectProjectHash: true,
            failPortCommit: true,
            allowInstallRoot: true);

        var result = await setup.Engine.ExecuteAsync(setup.Request, CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("deployment_failed", result.ErrorCode);
        Assert.True(setup.Activator.RollbackCalled);
        Assert.True(setup.PortRegistry.ReleaseCalled);
        Assert.False(File.Exists(Path.Combine(setup.InstallRoot, "current.release.json")));
        Assert.False(File.Exists(Path.Combine(setup.ManifestRoot, "sample.test.TEST-HOST.installed-state.json")));
        Assert.True(Directory.Exists(Path.Combine(setup.InstallRoot, ".failed", "op-install-failure")));
    }

    [Fact]
    public async Task InstallRootOutsideHostAllowlist_IsRejectedBeforeActivationPlan()
    {
        using var directory = new TestDirectory();
        var setup = await CreateSetupAsync(
            directory.FullPath,
            useCorrectProjectHash: true,
            failPortCommit: false,
            allowInstallRoot: false);

        var result = await setup.Engine.ExecuteAsync(setup.Request, CancellationToken.None);

        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("install_root_not_allowed", result.ErrorCode);
        Assert.Equal(0, setup.Activator.PlanCalls);
        Assert.False(Directory.Exists(Path.Combine(setup.InstallRoot, "releases")));
    }

    private static async Task<Setup> CreateSetupAsync(
        string root,
        bool useCorrectProjectHash,
        bool failPortCommit,
        bool allowInstallRoot)
    {
        var installRoot = Path.Combine(root, "install");
        var manifestRoot = Path.Combine(root, "manifests");
        Directory.CreateDirectory(manifestRoot);
        var projectPath = Path.Combine(manifestRoot, "project.json");
        await File.WriteAllTextAsync(
            projectPath,
            """
            {
              "manifestKind": "ProjectManifest",
              "metadata": { "id": "sample" },
              "components": [
                { "id": "api", "kind": "windowsService", "entrypoint": "api-main", "dependsOn": [], "health": [] }
              ],
              "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 5 }
            }
            """);
        var projectHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(projectPath))).ToLowerInvariant();
        var bindingPath = Path.Combine(manifestRoot, "binding.json");
        await File.WriteAllTextAsync(
            bindingPath,
            $$"""
            {
              "manifestKind": "EnvironmentBinding",
              "metadata": { "projectId": "sample", "environment": "test", "hostId": "TEST-HOST" },
              "roots": { "install": {{System.Text.Json.JsonSerializer.Serialize(installRoot)}} },
              "componentBindings": [ { "componentId": "api", "nativeName": "Company.Sample.Api" } ],
              "portBindings": []
            }
            """);

        var zipPath = Path.Combine(root, "package.zip");
        await using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            var entry = archive.CreateEntry("api/app.exe");
            await using var stream = entry.Open();
            await stream.WriteAsync("test binary"u8.ToArray());
        }

        var zipBytes = await File.ReadAllBytesAsync(zipPath);
        var zipHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        var releasePath = Path.Combine(root, "release.json");
        var declaredProjectHash = useCorrectProjectHash ? projectHash : new string('a', 64);
        await File.WriteAllTextAsync(
            releasePath,
            $$"""
            {
              "$schema": "release-manifest.schema.json",
              "apiVersion": "ops.company/v1",
              "manifestKind": "ReleaseManifest",
              "metadata": {
                "projectId": "sample", "version": "1.0.0",
                "releaseId": "sample-1.0.0-test", "builtAt": "2026-08-12T00:00:00Z"
              },
              "target": { "os": "windows", "architecture": "x64", "minAgentVersion": "0.1.0" },
              "projectManifestSha256": "{{declaredProjectHash}}",
              "artifacts": [
                {
                  "id": "package", "fileName": "package.zip", "mediaType": "application/zip",
                  "sha256": "{{zipHash}}", "sizeBytes": {{zipBytes.LongLength}}
                }
              ],
              "componentPayloads": [
                { "componentId": "api", "entrypoint": "api-main", "artifactId": "package", "path": "api/app.exe" }
              ]
            }
            """);

        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = manifestRoot,
            StateDirectory = Path.Combine(root, "state"),
            PipeName = "test",
            InventoryIntervalSeconds = 30,
            EnableMutations = true,
            AllowedProjectInstallRoots = [allowInstallRoot ? root : Path.Combine(root, "other-apps")]
        });
        var resolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var stateStore = new SqliteOpsStateStore(resolver, jsonOptions);
        await stateStore.InitializeAsync(CancellationToken.None);
        var catalog = new ManifestCatalogSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ManifestCatalogEntry(projectPath, "ProjectManifest", "sample", true, DateTimeOffset.UtcNow, []),
                new ManifestCatalogEntry(bindingPath, "EnvironmentBinding", "sample", true, DateTimeOffset.UtcNow, [])
            ]);
        var runtimeProject = new ProjectRuntimeView(
            "sample",
            "Sample",
            "test",
            ProjectBindingStatus.Declared,
            null,
            null,
            [
                new ProjectComponentRuntimeView(
                    "api",
                    "API",
                    "windowsService",
                    "Company.Sample.Api",
                    null,
                    ComponentOwnershipStatus.DeclaredOnly,
                    "unknown",
                    "unknown",
                    null)
            ],
            []);
        var cache = new AgentSnapshotCache();
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            catalog,
            new ProjectRegistrySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [runtimeProject]));
        var activator = new RecordingDeploymentActivator();
        var portRegistry = new FaultingPortRegistry(failPortCommit);
        var engine = new DeploymentEngine(
            cache,
            new ArtifactPackageValidator(resolver),
            new SafeZipExtractor(),
            portRegistry,
            activator,
            new OperationGate(),
            stateStore,
            resolver,
            options,
            jsonOptions);
        return new Setup(
            engine,
            new DeploymentRequest(
                failPortCommit ? "op-install-failure" : "op-install-hash",
                failPortCommit ? "idem-install-failure" : "idem-install-hash",
                "sample",
                "test",
                DeploymentAction.Install,
                0,
                releasePath,
                root),
            activator,
            portRegistry,
            stateStore,
            installRoot,
            manifestRoot);
    }

    private sealed record Setup(
        DeploymentEngine Engine,
        DeploymentRequest Request,
        RecordingDeploymentActivator Activator,
        FaultingPortRegistry PortRegistry,
        IOpsStateStore StateStore,
        string InstallRoot,
        string ManifestRoot);

    private sealed class RecordingDeploymentActivator : IDeploymentActivator
    {
        public int PlanCalls { get; private set; }
        public bool RollbackCalled { get; private set; }

        public Task<DeploymentActivationResult> PlanAsync(
            DeploymentActivationRequest request,
            CancellationToken cancellationToken)
        {
            PlanCalls++;
            return Task.FromResult(new DeploymentActivationResult(true, "planned"));
        }

        public Task<DeploymentActivationResult> ActivateAsync(
            DeploymentActivationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeploymentActivationResult(
                true,
                "activated",
                Rollback: new RecordingRollback(this)));

        private sealed class RecordingRollback(RecordingDeploymentActivator owner) : IDeploymentActivationRollback
        {
            public Task<DeploymentActivationResult> RestoreAsync(CancellationToken cancellationToken)
            {
                owner.RollbackCalled = true;
                return Task.FromResult(new DeploymentActivationResult(true, "restored"));
            }
        }
    }

    private sealed class FaultingPortRegistry(bool failCommit) : IPortRegistryStore
    {
        public bool ReleaseCalled { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PortReservationResult> ReserveAsync(
            IReadOnlyList<PortReservationRequest> requests,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PortReservationResult(true, requests));

        public Task ReleaseOperationAsync(string operationId, CancellationToken cancellationToken)
        {
            ReleaseCalled = true;
            return Task.CompletedTask;
        }

        public Task CommitOperationAsync(string operationId, CancellationToken cancellationToken) =>
            failCommit
                ? Task.FromException(new IOException("injected port commit failure"))
                : Task.CompletedTask;
    }
}
