using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class Pm2DeploymentEntrypointAdapterTests
{
    private const string OwnerSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string SnapshotFile = "CompanyOps.Pm2Bridge.TestOwner.v1.json";
    private const string PipeName = "CompanyOps.Pm2Bridge.TestOwner.v1";

    [Fact]
    public async Task FirstInstall_RegistersThenRollbackDeletesOnlyNewProcess()
    {
        using var directory = new TestDirectory();
        var unrelated = Identity(41, "other-project", Path.Combine(directory.FullPath, "other"), "other.js", ["--safe"]);
        var host = CreateHost(directory.FullPath, [unrelated]);
        var target = Target(directory.FullPath, "candidate");

        var capture = await host.Adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);
        Assert.Null(capture.Snapshot!.PmId);

        var applied = await host.Adapter.ApplyAsync(target, capture.Snapshot, CancellationToken.None);
        Assert.True(applied.Success, applied.Detail);
        Assert.NotNull(applied.PmId);
        Assert.Contains(host.Bridge.Processes, item => item.PmId == applied.PmId && item.Name == target.NativeName);

        var restored = await host.Adapter.RestoreAsync(capture.Snapshot, CancellationToken.None);
        Assert.True(restored.Success, restored.Detail);
        Assert.Equal([unrelated], host.Bridge.Processes);
        Assert.Equal(["register:sample-worker", $"delete:{applied.PmId}"], host.Bridge.Commands);
        AssertNoBroadCommands(host.Bridge.Commands);
    }

    [Fact]
    public async Task ExactUpdate_ReplacesOldIdentityAndRollbackRestoresIt()
    {
        using var directory = new TestDirectory();
        var old = CreateCurrentRelease(directory.FullPath);
        var unrelated = Identity(42, "other-project", Path.Combine(directory.FullPath, "other"), "other.js", []);
        var host = CreateHost(directory.FullPath, [old, unrelated]);
        var target = Target(directory.FullPath, "candidate");

        var capture = await host.Adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);
        Assert.Equal(old.PmId, capture.Snapshot!.PmId);

        var applied = await host.Adapter.ApplyAsync(target, capture.Snapshot, CancellationToken.None);
        Assert.True(applied.Success, applied.Detail);
        Assert.DoesNotContain(host.Bridge.Processes, item => item.PmId == old.PmId);
        Assert.Contains(host.Bridge.Processes, item => item.PmId == applied.PmId && SameIdentity(item, target));

        var restored = await host.Adapter.RestoreAsync(capture.Snapshot, CancellationToken.None);
        Assert.True(restored.Success, restored.Detail);
        Assert.Contains(host.Bridge.Processes, item => SameIdentity(item, old));
        Assert.Contains(unrelated, host.Bridge.Processes);
        Assert.Equal(2, host.Bridge.Commands.Count(command => command.StartsWith("register:", StringComparison.Ordinal)));
        Assert.Equal(2, host.Bridge.Commands.Count(command => command.StartsWith("delete:", StringComparison.Ordinal)));
        AssertNoBroadCommands(host.Bridge.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_RejectsSameNameDifferentPathOrMultipleInstances(bool duplicate)
    {
        using var directory = new TestDirectory();
        var old = CreateCurrentRelease(directory.FullPath);
        var wrong = old with { PmId = 70, Cwd = Path.Combine(directory.FullPath, "wrong") };
        var processes = duplicate ? new[] { old, old with { PmId = 71 } } : new[] { wrong };
        var host = CreateHost(directory.FullPath, processes);

        var result = await host.Adapter.CaptureAsync(Target(directory.FullPath, "candidate"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(host.Bridge.Commands);
    }

    [Fact]
    public async Task Capture_RejectsStaleSnapshotWithoutMutation()
    {
        using var directory = new TestDirectory();
        var old = CreateCurrentRelease(directory.FullPath);
        var host = CreateHost(directory.FullPath, [old], DateTimeOffset.UtcNow.AddMinutes(-2));

        var result = await host.Adapter.CaptureAsync(Target(directory.FullPath, "candidate"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("过期", result.Detail, StringComparison.Ordinal);
        Assert.Empty(host.Bridge.Commands);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("health")]
    public async Task ActivationFailure_RestoresOldIdentityAndLeavesOtherProcessesUnchanged(string failureStage)
    {
        using var directory = new TestDirectory();
        var old = CreateCurrentRelease(directory.FullPath);
        var unrelated = Identity(88, "other-project", Path.Combine(directory.FullPath, "other"), "other.js", ["--other"]);
        var host = CreateHost(directory.FullPath, [old, unrelated]);
        host.Bridge.FailNextRegistration = failureStage == "register";
        var control = new RecordingControl();
        var health = new RecordingHealth(failureStage == "health");
        var activator = new NativeDeploymentActivator([host.Adapter], [control], health);
        var request = ActivationRequest(directory.FullPath);

        var result = await activator.ActivateAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(host.Bridge.Processes, item => SameIdentity(item, old));
        Assert.Contains(unrelated, host.Bridge.Processes);
        Assert.Single(host.Bridge.Processes, item => item.Name == old.Name);
        Assert.DoesNotContain(host.Bridge.Processes, item =>
            SamePath(item.Script, Path.Combine(directory.FullPath, "candidate", "package", "app", "worker.js")));
        Assert.DoesNotContain(control.Calls, call =>
            call.Action == ComponentOperationAction.Start &&
            call.PmId is int pmId && host.Bridge.CandidatePmIds.Contains(pmId));
        AssertNoBroadCommands(host.Bridge.Commands);
    }

    [Fact]
    public async Task FirstInstall_RegistersInTopologyOrderAndHealthGatesDownstreamWithoutControlStart()
    {
        using var directory = new TestDirectory();
        var host = CreateHost(directory.FullPath, []);
        var releasePath = Path.Combine(directory.FullPath, "candidate");
        foreach (var component in new[] { "api", "gateway" })
        {
            var componentRoot = Path.Combine(releasePath, "package", component);
            Directory.CreateDirectory(componentRoot);
            File.WriteAllText(Path.Combine(componentRoot, "app.js"), "// fixture");
        }
        var control = new RecordingControl();
        var health = new RecordingHealth(false, host.Bridge.Timeline);
        var activator = new NativeDeploymentActivator([host.Adapter], [control], health);
        var project = JsonNode.Parse("""
        { "components": [
          { "id":"api", "kind":"pm2Legacy", "entrypoint":"api-main", "dependsOn":[], "health":[],
            "pm2":{"name":"sample-api","cwd":"api","script":"api/app.js"} },
          { "id":"gateway", "kind":"pm2Legacy", "entrypoint":"gateway-main", "dependsOn":["api"], "health":[],
            "pm2":{"name":"sample-gateway","cwd":"gateway","script":"gateway/app.js"} }
        ], "update":{"healthTimeoutSeconds":5} }
        """)!.AsObject();
        var release = JsonNode.Parse("""
        { "componentPayloads": [
          { "componentId":"api", "entrypoint":"api-main", "artifactId":"package", "path":"api/app.js", "workingDirectory":"api", "arguments":[],
            "pm2":{"name":"sample-api","cwd":"api","script":"api/app.js","arguments":[]} },
          { "componentId":"gateway", "entrypoint":"gateway-main", "artifactId":"package", "path":"gateway/app.js", "workingDirectory":"gateway", "arguments":[],
            "pm2":{"name":"sample-gateway","cwd":"gateway","script":"gateway/app.js","arguments":[]} }
        ] }
        """)!.AsObject();
        var binding = JsonNode.Parse($$"""
        { "roots":{"install":{{JsonSerializer.Serialize(Path.Combine(directory.FullPath, "install"))}}},
          "componentBindings":[
            {"componentId":"api","nativeName":"sample-api"},
            {"componentId":"gateway","nativeName":"sample-gateway"}],
          "portBindings":[],
          "legacyPm2":{"ownerSid":"{{OwnerSid}}","snapshotFileName":"{{SnapshotFile}}","controlPipeName":"{{PipeName}}","maxAgeSeconds":30} }
        """)!.AsObject();

        var result = await activator.ActivateAsync(
            new DeploymentActivationRequest("sample", "test", releasePath, project, release, binding),
            CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(
            ["register:sample-api", "health:api", "register:sample-gateway", "health:gateway"],
            host.Bridge.Timeline);
        Assert.DoesNotContain(ComponentOperationAction.Start, control.Calls.Select(call => call.Action));
        AssertNoBroadCommands(host.Bridge.Commands);
    }

    private static TestHost CreateHost(
        string root,
        IReadOnlyList<Pm2BridgeProcessIdentity> processes,
        DateTimeOffset? capturedAt = null)
    {
        var snapshotRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(snapshotRoot);
        var snapshotPath = Path.Combine(snapshotRoot, SnapshotFile);
        var bridge = new StatefulMutationBridge(snapshotPath, processes, capturedAt);
        bridge.WriteSnapshot();
        var options = Options.Create(new OpsOptions
        {
            HostId = "TEST-HOST",
            ManifestDirectory = Path.Combine(root, "manifests"),
            StateDirectory = Path.Combine(root, "state"),
            Pm2SnapshotDirectory = snapshotRoot
        });
        var reader = new Pm2SnapshotReader(new OpsPathResolver(options), TestDirectory.CreateJsonOptions());
        return new TestHost(new Pm2LegacyDeploymentEntrypointAdapter(reader, bridge), bridge);
    }

    private static DeploymentEntrypointTarget Target(string root, string releaseName)
    {
        var release = Path.Combine(root, releaseName);
        var cwd = Path.Combine(release, "package", "app");
        Directory.CreateDirectory(cwd);
        var script = Path.Combine(cwd, "worker.js");
        File.WriteAllText(script, "// fixture");
        return new DeploymentEntrypointTarget(
            "sample", "test", "worker", "pm2Legacy", "sample-worker", script,
            $"\"{script}\" --port 19001", cwd, ["--port", "19001"],
            SnapshotFile, PipeName, OwnerSid, 30, Path.Combine(root, "install"),
            new Dictionary<string, string>());
    }

    private static Pm2BridgeProcessIdentity CreateCurrentRelease(string root)
    {
        var installRoot = Path.Combine(root, "install");
        var releaseRoot = Path.Combine(installRoot, "releases", "1.0.0");
        var cwd = Path.Combine(releaseRoot, "package", "app");
        Directory.CreateDirectory(cwd);
        var script = Path.Combine(cwd, "worker.js");
        File.WriteAllText(script, "// old fixture");
        var manifestRoot = Path.Combine(releaseRoot, ".companyops");
        Directory.CreateDirectory(manifestRoot);
        File.WriteAllText(Path.Combine(manifestRoot, "release-manifest.json"),
            """
            {
              "componentPayloads": [{
                "componentId": "worker", "artifactId": "package",
                "pm2": { "name": "sample-worker", "cwd": "app", "script": "app/worker.js", "arguments": ["--port", "19001"] }
              }]
            }
            """);
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "current.release.json"),
            JsonSerializer.Serialize(new { currentPath = releaseRoot }));
        return Identity(7, "sample-worker", cwd, "worker.js", ["--port", "19001"]);
    }

    private static DeploymentActivationRequest ActivationRequest(string root)
    {
        var target = Target(root, "candidate");
        var releasePath = Path.Combine(root, "candidate");
        var project = JsonNode.Parse("""
        {
          "components": [{
            "id": "worker", "kind": "pm2Legacy", "entrypoint": "worker-main", "dependsOn": [], "health": [],
            "pm2": { "name": "sample-worker", "cwd": "app", "script": "app/worker.js" }
          }],
          "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 5 }
        }
        """)!.AsObject();
        var release = JsonNode.Parse("""
        {
          "componentPayloads": [{
            "componentId": "worker", "entrypoint": "worker-main", "artifactId": "package",
            "path": "app/worker.js", "workingDirectory": "app", "arguments": [],
            "pm2": { "name": "sample-worker", "cwd": "app", "script": "app/worker.js", "arguments": [] }
          }]
        }
        """)!.AsObject();
        var binding = JsonNode.Parse($$"""
        {
          "roots": { "install": {{JsonSerializer.Serialize(Path.Combine(root, "install"))}} },
          "componentBindings": [{ "componentId": "worker", "nativeName": "sample-worker" }],
          "portBindings": [],
          "legacyPm2": {
            "ownerSid": "{{OwnerSid}}", "snapshotFileName": "{{SnapshotFile}}",
            "controlPipeName": "{{PipeName}}", "maxAgeSeconds": 30
          }
        }
        """)!.AsObject();
        return new DeploymentActivationRequest("sample", "test", releasePath, project, release, binding);
    }

    private static Pm2BridgeProcessIdentity Identity(
        int pmId, string name, string cwd, string scriptName, IReadOnlyList<string> arguments) =>
        new(pmId, name, cwd, Path.IsPathFullyQualified(scriptName) ? scriptName : Path.Combine(cwd, scriptName), arguments);

    private static bool SameIdentity(Pm2BridgeProcessIdentity left, Pm2BridgeProcessIdentity right) =>
        left.Name == right.Name && SamePath(left.Cwd, right.Cwd) && SamePath(left.Script, right.Script) &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

    private static bool SameIdentity(Pm2BridgeProcessIdentity identity, DeploymentEntrypointTarget target) =>
        identity.Name == target.NativeName && SamePath(identity.Cwd, target.WorkingDirectory!) &&
        SamePath(identity.Script, target.ExecutablePath) && identity.Arguments.SequenceEqual(target.Arguments, StringComparer.Ordinal);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void AssertNoBroadCommands(IEnumerable<string> commands) =>
        Assert.DoesNotContain(commands, command =>
            command.Contains("all", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("kill", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("PM2_HOME", StringComparison.OrdinalIgnoreCase));

    private sealed record TestHost(Pm2LegacyDeploymentEntrypointAdapter Adapter, StatefulMutationBridge Bridge);

    private sealed class StatefulMutationBridge : IPm2OwnerMutationBridge
    {
        private readonly string _snapshotPath;
        private readonly DateTimeOffset? _fixedCapturedAt;
        private int _nextPmId;

        public StatefulMutationBridge(
            string snapshotPath,
            IReadOnlyList<Pm2BridgeProcessIdentity> processes,
            DateTimeOffset? fixedCapturedAt)
        {
            _snapshotPath = snapshotPath;
            _fixedCapturedAt = fixedCapturedAt;
            Processes = processes.ToList();
            _nextPmId = Processes.Select(item => item.PmId).DefaultIfEmpty(100).Max() + 1;
        }

        public List<Pm2BridgeProcessIdentity> Processes { get; }
        public List<string> Commands { get; } = [];
        public List<string> Timeline { get; } = [];
        public HashSet<int> CandidatePmIds { get; } = [];
        public bool FailNextRegistration { get; set; }

        public Task<Pm2BridgeMutationResponse> ExecuteAsync(
            string pipeName,
            Pm2BridgeMutationRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(PipeName, pipeName);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation == Pm2BridgeMutationAction.Delete && request.ExpectedProcess is { } expected)
            {
                var matches = Processes.Where(item => item.PmId == expected.PmId && SameIdentity(item, expected)).ToArray();
                if (matches.Length != 1)
                    return Task.FromResult(Response(request, false, null, "exact identity mismatch"));
                Processes.Remove(matches[0]);
                Commands.Add($"delete:{expected.PmId}");
                WriteSnapshot();
                return Task.FromResult(Response(request, true, expected, "deleted"));
            }

            if (request.Operation == Pm2BridgeMutationAction.Register && request.Registration is { } registration)
            {
                if (FailNextRegistration)
                {
                    FailNextRegistration = false;
                    Commands.Add($"register-rejected:{registration.Name}");
                    return Task.FromResult(Response(request, false, null, "injected registration failure"));
                }
                if (Processes.Any(item => item.Name == registration.Name))
                    return Task.FromResult(Response(request, false, null, "duplicate name"));
                var created = new Pm2BridgeProcessIdentity(
                    _nextPmId++, registration.Name, registration.Cwd, registration.Script, registration.Arguments);
                Processes.Add(created);
                if (registration.Script.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                    CandidatePmIds.Add(created.PmId);
                Commands.Add($"register:{registration.Name}");
                Timeline.Add($"register:{registration.Name}");
                WriteSnapshot();
                return Task.FromResult(Response(request, true, created, "registered"));
            }

            return Task.FromResult(Response(request, false, null, "invalid mutation"));
        }

        public void WriteSnapshot()
        {
            var snapshot = new Pm2Snapshot(
                Pm2SnapshotProtocol.Version, OwnerSid, _fixedCapturedAt ?? DateTimeOffset.UtcNow, 123,
                Processes.Select(item => new Pm2ProcessSnapshot(
                    item.Name, item.PmId, item.Cwd, item.Script, "online", item.PmId + 1000, 0, item.Arguments)).ToArray(),
                PipeName);
            File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(snapshot, TestDirectory.CreateJsonOptions()));
        }

        private static Pm2BridgeMutationResponse Response(
            Pm2BridgeMutationRequest request, bool success, Pm2BridgeProcessIdentity? process, string detail) =>
            new(Pm2BridgeProtocol.MutationVersion, request.RequestId, success, process,
                success ? null : "fake_rejected", detail);
    }

    private sealed class RecordingControl : IComponentControlAdapter
    {
        public string Kind => "pm2Legacy";
        public List<(ComponentOperationAction Action, int? PmId)> Calls { get; } = [];

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken)
        {
            Calls.Add((action, target.PmId));
            return Task.FromResult(new AdapterExecutionResult(true, "controlled"));
        }
    }

    private sealed class RecordingHealth(bool fail, List<string>? timeline = null) : IManifestHealthGate
    {
        public Task<HealthGateResult> ProbeAsync(
            JsonObject projectManifest,
            JsonObject binding,
            string componentId,
            CancellationToken cancellationToken)
        {
            timeline?.Add($"health:{componentId}");
            return fail
                ? Task.FromException<HealthGateResult>(new IOException("injected health failure"))
                : Task.FromResult(new HealthGateResult(true, "healthy"));
        }
    }
}
