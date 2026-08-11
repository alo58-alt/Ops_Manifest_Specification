using System.IO.Compression;
using System.Security.Cryptography;
using CompanyOps.Agent;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class DeploymentSafetyTests
{
    [Fact]
    public async Task ArtifactHashAndSafeZipExtraction_Succeed()
    {
        using var directory = new TestDirectory();
        var zipPath = Path.Combine(directory.FullPath, "package.zip");
        await CreateZipAsync(zipPath, "app/service.txt", "payload");
        var manifest = await WriteReleaseManifestAsync(directory.FullPath, zipPath);
        var validator = CreateArtifactValidator(directory.FullPath);

        var validation = await validator.ValidateAsync(manifest, directory.FullPath, CancellationToken.None);
        var destination = Path.Combine(directory.FullPath, "extract");
        await new SafeZipExtractor().ExtractAsync(Assert.Single(validation.Artifacts), destination, CancellationToken.None);

        Assert.True(validation.Success);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(destination, "app", "service.txt")));
    }

    [Fact]
    public async Task ZipPathTraversal_IsRejectedWithoutWritingOutsideDestination()
    {
        using var directory = new TestDirectory();
        var zipPath = Path.Combine(directory.FullPath, "package.zip");
        await CreateZipAsync(zipPath, "../escape.txt", "bad");
        var manifest = await WriteReleaseManifestAsync(directory.FullPath, zipPath);
        var validation = await CreateArtifactValidator(directory.FullPath).ValidateAsync(
            manifest,
            directory.FullPath,
            CancellationToken.None);
        var destination = Path.Combine(directory.FullPath, "extract");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SafeZipExtractor().ExtractAsync(
                Assert.Single(validation.Artifacts),
                destination,
                CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory.FullPath, "escape.txt")));
    }

    [Fact]
    public async Task ReleaseManifestMissingRequiredTarget_FailsBeforeArtifactUse()
    {
        using var directory = new TestDirectory();
        var manifest = Path.Combine(directory.FullPath, "release.json");
        File.WriteAllText(
            manifest,
            """
            { "manifestKind": "ReleaseManifest", "metadata": { "projectId": "sample", "version": "1.0.0" } }
            """);

        var result = await CreateArtifactValidator(directory.FullPath).ValidateAsync(
            manifest,
            directory.FullPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, static error => error.Contains("Schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PortRegistry_WildcardConflictsAndBatchIsAtomic()
    {
        using var directory = new TestDirectory();
        var store = CreatePortStore(directory.FullPath);
        await store.InitializeAsync(CancellationToken.None);
        var first = Reservation("0.0.0.0", 9201, "project-a", "op-a");
        var reserved = await store.ReserveAsync([first], CancellationToken.None);

        var conflict = await store.ReserveAsync(
            [Reservation("127.0.0.1", 9201, "project-b", "op-b")],
            CancellationToken.None);
        var unrelated = await store.ReserveAsync(
            [Reservation("127.0.0.1", 9202, "project-b", "op-c")],
            CancellationToken.None);

        Assert.True(reserved.Success);
        Assert.False(conflict.Success);
        Assert.Equal("port_conflict", conflict.ErrorCode);
        Assert.True(unrelated.Success);
    }

    [Fact]
    public async Task DeploymentEngine_StagesCommitsPointerStateAndPortsInTempRoot()
    {
        using var directory = new TestDirectory();
        var installRoot = Path.Combine(directory.FullPath, "install");
        var manifestRoot = Path.Combine(directory.FullPath, "manifests");
        Directory.CreateDirectory(manifestRoot);
        var projectPath = Path.Combine(manifestRoot, "project.json");
        var bindingPath = Path.Combine(manifestRoot, "binding.json");
        File.WriteAllText(
            projectPath,
            """
            {
              "manifestKind": "ProjectManifest",
              "metadata": { "id": "sample" },
              "components": [ { "id": "api", "kind": "windowsService" } ]
            }
            """);
        File.WriteAllText(
            bindingPath,
            $$"""
            {
              "manifestKind": "EnvironmentBinding",
              "metadata": { "projectId": "sample", "environment": "test", "hostId": "TEST-HOST" },
              "roots": { "install": {{System.Text.Json.JsonSerializer.Serialize(installRoot)}} },
              "componentBindings": [ { "componentId": "api", "nativeName": "Company.Sample.Api" } ],
              "portBindings": [
                { "protocol": "tcp", "address": "127.0.0.1", "port": 19201, "componentId": "api", "portId": "http" }
              ]
            }
            """);

        var zipPath = Path.Combine(directory.FullPath, "package.zip");
        await CreateZipAsync(zipPath, "api/app.exe", "binary");
        var bytes = File.ReadAllBytes(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var releasePath = Path.Combine(directory.FullPath, "release.json");
        File.WriteAllText(
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
              "projectManifestSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "artifacts": [
                {
                  "id": "package", "fileName": "package.zip", "mediaType": "application/zip",
                  "sha256": "{{hash}}", "sizeBytes": {{bytes.LongLength}}
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
            StateDirectory = Path.Combine(directory.FullPath, "state"),
            PipeName = "test",
            InventoryIntervalSeconds = 30,
            EnableMutations = true
        });
        var resolver = new OpsPathResolver(options);
        var jsonOptions = TestDirectory.CreateJsonOptions();
        var stateStore = new SqliteOpsStateStore(resolver, jsonOptions);
        var portStore = new SqlitePortRegistryStore(resolver);
        await stateStore.InitializeAsync(CancellationToken.None);
        await portStore.InitializeAsync(CancellationToken.None);
        var catalog = new ManifestCatalogSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ManifestCatalogEntry(projectPath, "ProjectManifest", "sample", true, DateTimeOffset.UtcNow, []),
                new ManifestCatalogEntry(bindingPath, "EnvironmentBinding", "sample", true, DateTimeOffset.UtcNow, [])
            ]);
        var runtimeProject = new ProjectRuntimeView(
            "sample", "Sample", "test", ProjectBindingStatus.Declared, null, null,
            [new ProjectComponentRuntimeView(
                "api", "API", "windowsService", "Company.Sample.Api", null,
                ComponentOwnershipStatus.DeclaredOnly, "unknown", "unknown", null)],
            []);
        var cache = new AgentSnapshotCache();
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            catalog,
            new ProjectRegistrySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [runtimeProject]));
        var engine = new DeploymentEngine(
            cache,
            new ArtifactPackageValidator(resolver),
            new SafeZipExtractor(),
            portStore,
            new ReleasePointerDeploymentActivator(),
            new OperationGate(),
            stateStore,
            resolver,
            options,
            jsonOptions);

        var result = await engine.ExecuteAsync(
            new DeploymentRequest(
                "op-install-1", "idem-install-1", "sample", "test",
                DeploymentAction.Install, 0, releasePath, directory.FullPath),
            CancellationToken.None);
        var repeated = await engine.ExecuteAsync(
            new DeploymentRequest(
                "op-install-1", "idem-install-1", "sample", "test",
                DeploymentAction.Install, 0, releasePath, directory.FullPath),
            CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(result, repeated);
        Assert.True(File.Exists(Path.Combine(installRoot, "releases", "1.0.0", "package", "api", "app.exe")));
        Assert.True(File.Exists(Path.Combine(installRoot, "current.release.json")));
        Assert.True(File.Exists(Path.Combine(manifestRoot, "sample.test.TEST-HOST.installed-state.json")));

        var previousRelease = Path.Combine(installRoot, "releases", "0.9.0");
        Directory.CreateDirectory(Path.Combine(previousRelease, ".companyops"));
        File.WriteAllText(
            Path.Combine(previousRelease, ".companyops", "release-manifest.json"),
            """{ "metadata": { "projectId": "sample", "version": "0.9.0" } }""");
        File.WriteAllText(
            Path.Combine(installRoot, "current.release.json"),
            $$"""
            {
              "currentVersion": "1.0.0",
              "currentPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(installRoot, "releases", "1.0.0"))}},
              "previousVersion": "0.9.0",
              "previousPath": {{System.Text.Json.JsonSerializer.Serialize(previousRelease)}}
            }
            """);
        var installedProject = runtimeProject with
        {
            Status = ProjectBindingStatus.Installed,
            InstalledVersion = "1.0.0",
            Generation = 1,
            Components =
            [
                runtimeProject.Components[0] with
                {
                    InstalledNativeId = "Company.Sample.Api",
                    Ownership = ComponentOwnershipStatus.Owned
                }
            ]
        };
        cache.Update(
            new InventorySnapshot("TEST-HOST", DateTimeOffset.UtcNow, []),
            catalog,
            new ProjectRegistrySnapshot("TEST-HOST", DateTimeOffset.UtcNow, [installedProject]));

        var rollback = await engine.ExecuteAsync(
            new DeploymentRequest(
                "op-rollback-1", "idem-rollback-1", "sample", "test",
                DeploymentAction.Rollback, 1),
            CancellationToken.None);

        Assert.Equal(OperationOutcome.Succeeded, rollback.Outcome);
        using var pointerDocument = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(installRoot, "current.release.json")));
        Assert.Equal("0.9.0", pointerDocument.RootElement.GetProperty("currentVersion").GetString());
        using var stateDocument = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(manifestRoot, "sample.test.TEST-HOST.installed-state.json")));
        Assert.Equal("0.9.0", stateDocument.RootElement.GetProperty("release").GetProperty("version").GetString());
        Assert.Equal(2, stateDocument.RootElement.GetProperty("metadata").GetProperty("generation").GetInt64());
    }

    private static SqlitePortRegistryStore CreatePortStore(string path)
    {
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = path,
            StateDirectory = path,
            PipeName = "test",
            InventoryIntervalSeconds = 30
        });
        return new SqlitePortRegistryStore(new OpsPathResolver(options));
    }

    private static ArtifactPackageValidator CreateArtifactValidator(string path)
    {
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = path,
            StateDirectory = path,
            PipeName = "test",
            InventoryIntervalSeconds = 30
        });
        return new ArtifactPackageValidator(new OpsPathResolver(options));
    }

    private static PortReservationRequest Reservation(
        string address,
        int port,
        string projectId,
        string operationId) =>
        new("tcp", address, port, projectId, "test", "api", "http", operationId);

    private static async Task CreateZipAsync(string path, string entryName, string content)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry(entryName);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        await writer.WriteAsync(content);
    }

    private static async Task<string> WriteReleaseManifestAsync(string directory, string zipPath)
    {
        var bytes = await File.ReadAllBytesAsync(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifestPath = Path.Combine(directory, "release.json");
        await File.WriteAllTextAsync(
            manifestPath,
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
              "projectManifestSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "artifacts": [
                {
                  "id": "package",
                  "fileName": "package.zip",
                  "mediaType": "application/zip",
                  "sha256": "{{hash}}",
                  "sizeBytes": {{bytes.LongLength}}
                }
              ],
              "componentPayloads": [
                { "componentId": "api", "entrypoint": "api-main", "artifactId": "package", "path": "content" }
              ]
            }
            """);
        return manifestPath;
    }
}
