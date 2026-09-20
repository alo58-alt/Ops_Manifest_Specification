using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Tests;

public sealed class WindowsServiceDeploymentEntrypointAdapterTests
{
    [Fact]
    public async Task FirstInstall_CreateAndRollbackDeletesOnlyServiceCreatedByThisOperation()
    {
        using var directory = new TestDirectory();
        var unrelated = Observed("Other.Product", "other.exe");
        var services = new StatefulWindowsServiceManager([unrelated]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);
        var target = CreateTarget(directory.FullPath, "api", "Company.Sample.Api", allowCreate: true);

        var capture = await adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);
        Assert.False(capture.Snapshot!.Existed);

        var applied = await adapter.ApplyAsync(target, capture.Snapshot, CancellationToken.None);
        Assert.True(applied.Success, applied.Detail);
        Assert.Single(services.FindExact(target.NativeName));

        var restored = await adapter.RestoreAsync(capture.Snapshot, CancellationToken.None);
        Assert.True(restored.Success, restored.Detail);
        Assert.Empty(services.FindExact(target.NativeName));
        Assert.Equal(unrelated, Assert.Single(services.FindExact(unrelated.ServiceName)));
        Assert.Equal(["create:Company.Sample.Api", "delete:Company.Sample.Api"], services.Events);
    }

    [Fact]
    public async Task ThreeServiceInstall_WhenSecondCreateFails_RollsBackFirstAndNeverCreatesThird()
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([])
        {
            FailCreateServiceName = "Company.Sample.Converter"
        };
        var activator = CreateActivator(services, new RecordingHealthGate());

        var result = await activator.ActivateAsync(
            CreateActivationRequest(directory.FullPath, allowCreate: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(services.Services);
        Assert.Equal(
            [
                "create:Company.Sample.Engine",
                "create-failed:Company.Sample.Converter",
                "delete:Company.Sample.Engine"
            ],
            services.Events);
        Assert.DoesNotContain(services.Events, value => value.Contains("Gateway", StringComparison.Ordinal));
        Assert.DoesNotContain("Company.Sample.Gateway", services.DeleteAttempts);
    }

    [Fact]
    public async Task HealthFailure_RemovesEveryServiceCreatedByThisInstall()
    {
        using var directory = new TestDirectory();
        var unrelated = Observed("Other.Product", "other.exe");
        var services = new StatefulWindowsServiceManager([unrelated]);
        var activator = CreateActivator(services, new RecordingHealthGate("converter"));

        var result = await activator.ActivateAsync(
            CreateActivationRequest(directory.FullPath, allowCreate: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(unrelated, Assert.Single(services.Services));
        Assert.Equal(
            ["Company.Sample.Gateway", "Company.Sample.Converter", "Company.Sample.Engine"],
            services.Events.Where(value => value.StartsWith("delete:", StringComparison.Ordinal))
                .Select(value => value["delete:".Length..]).ToArray());
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Rollback")]
    public async Task MissingService_WhenCreationIsNotAllowed_FailsClosed(string action)
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);
        var target = CreateTarget(directory.FullPath, action, $"Company.Sample.{action}", allowCreate: false);

        var capture = await adapter.CaptureAsync(target, CancellationToken.None);

        Assert.False(capture.Success);
        Assert.Contains("不允许补建", capture.Detail, StringComparison.Ordinal);
        Assert.Empty(services.Events);
    }

    [Fact]
    public async Task ReplayingCreateSnapshot_DoesNotCreateASecondServiceAndFailsClosed()
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);
        var target = CreateTarget(directory.FullPath, "api", "Company.Sample.Api", allowCreate: true);
        var capture = await adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);

        var first = await adapter.ApplyAsync(target, capture.Snapshot!, CancellationToken.None);
        var repeatedCapture = await adapter.CaptureAsync(target, CancellationToken.None);
        var repeatedApply = await adapter.ApplyAsync(target, capture.Snapshot!, CancellationToken.None);

        Assert.True(first.Success, first.Detail);
        Assert.True(repeatedCapture.Success, repeatedCapture.Detail);
        Assert.True(repeatedCapture.Snapshot!.Existed);
        Assert.False(repeatedApply.Success);
        Assert.Single(services.FindExact(target.NativeName));
        Assert.Equal(2, services.CreateAttempts);
    }

    [Fact]
    public async Task ExternalSameConfigurationCreatedAfterCapture_IsNeverDeletedWithoutOwnershipMarker()
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);
        var target = CreateTarget(directory.FullPath, "api", "Company.Sample.Api", allowCreate: true);
        var capture = await adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);

        services.Replace(target.NativeName, FromTarget(target));
        var applied = await adapter.ApplyAsync(target, capture.Snapshot!, CancellationToken.None);
        var restored = await adapter.RestoreAsync(capture.Snapshot!, CancellationToken.None);

        Assert.False(applied.Success);
        Assert.False(restored.Success);
        Assert.Single(services.FindExact(target.NativeName));
        Assert.DoesNotContain($"delete:{target.NativeName}", services.Events);
    }

    [Fact]
    public async Task ExistingServiceUpdate_ChangesOnlyImmutableReleaseEntrypointAndRestoresIt()
    {
        using var directory = new TestDirectory();
        var target = CreateTarget(directory.FullPath, "api", "Company.Sample.Api", allowCreate: false);
        var specification = target.WindowsService!;
        var oldBinaryPath = "\"C:\\CompanyOps\\releases\\1.0.0\\ServiceHost.exe\" --component api";
        var services = new StatefulWindowsServiceManager([
            new WindowsServiceObservedState(
                specification.ServiceName,
                specification.DisplayName,
                oldBinaryPath,
                true,
                specification.StartMode,
                specification.ServiceAccountName,
                specification.Dependencies,
                specification.FailureRestartLimit)
        ]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);

        var capture = await adapter.CaptureAsync(target, CancellationToken.None);
        var applied = await adapter.ApplyAsync(target, capture.Snapshot!, CancellationToken.None);
        var restored = await adapter.RestoreAsync(capture.Snapshot!, CancellationToken.None);

        Assert.True(capture.Success, capture.Detail);
        Assert.True(capture.Snapshot!.Existed);
        Assert.True(applied.Success, applied.Detail);
        Assert.True(restored.Success, restored.Detail);
        Assert.Equal(oldBinaryPath, Assert.Single(services.FindExact(specification.ServiceName)).BinaryPath);
        Assert.Equal(
            [$"change:{specification.ServiceName}", $"change:{specification.ServiceName}"],
            services.Events);
        Assert.DoesNotContain(services.Events, value => value.StartsWith("create:", StringComparison.Ordinal));
        Assert.DoesNotContain(services.Events, value => value.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollbackRefusesDeletionWhenCreatedServiceIdentityHasChanged()
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([]);
        var adapter = new WindowsServiceDeploymentEntrypointAdapter(services);
        var target = CreateTarget(directory.FullPath, "api", "Company.Sample.Api", allowCreate: true);
        var capture = await adapter.CaptureAsync(target, CancellationToken.None);
        Assert.True(capture.Success, capture.Detail);
        Assert.True((await adapter.ApplyAsync(target, capture.Snapshot!, CancellationToken.None)).Success);
        services.Replace(target.NativeName, services.FindExact(target.NativeName).Single() with
        {
            BinaryPath = "\"C:\\Unexpected\\other.exe\""
        });

        var restored = await adapter.RestoreAsync(capture.Snapshot!, CancellationToken.None);

        Assert.False(restored.Success);
        Assert.Single(services.FindExact(target.NativeName));
        Assert.DoesNotContain($"delete:{target.NativeName}", services.Events);
    }

    [Fact]
    public async Task WebQuizBotLikeFirstInstall_StartsInteractiveAppOnlyAfterApiHealth()
    {
        using var directory = new TestDirectory();
        var services = new StatefulWindowsServiceManager([]);
        var events = services.Events;
        var activator = new NativeDeploymentActivator(
            [
                new WindowsServiceDeploymentEntrypointAdapter(services),
                new TimelineEntrypointAdapter("interactiveApp", events)
            ],
            [
                new TimelineControlAdapter("windowsService", events),
                new TimelineControlAdapter("interactiveApp", events)
            ],
            new TimelineHealthGate(events));

        var result = await activator.ActivateAsync(
            CreateWebQuizBotLikeRequest(directory.FullPath),
            CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(
            [
                "capture:browser-host",
                "control:Stop:browser-host",
                "create:WebQuizBot",
                "apply:browser-host",
                "control:Start:api",
                "health:api",
                "control:Start:browser-host",
                "health:browser-host"
            ],
            events);
    }

    [Fact]
    public async Task WebQuizBotLikeInteractiveFailure_DeletesOnlyNewApiService()
    {
        using var directory = new TestDirectory();
        var unrelated = Observed("Other.Product", "other.exe");
        var services = new StatefulWindowsServiceManager([unrelated]);
        var events = services.Events;
        var activator = new NativeDeploymentActivator(
            [
                new WindowsServiceDeploymentEntrypointAdapter(services),
                new TimelineEntrypointAdapter("interactiveApp", events)
            ],
            [
                new TimelineControlAdapter("windowsService", events),
                new TimelineControlAdapter("interactiveApp", events)
            ],
            new TimelineHealthGate(events, "browser-host"));

        var result = await activator.ActivateAsync(
            CreateWebQuizBotLikeRequest(directory.FullPath),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(unrelated, Assert.Single(services.Services));
        Assert.Contains("delete:WebQuizBot", events);
        Assert.Contains("restore:browser-host", events);
    }

    private static NativeDeploymentActivator CreateActivator(
        StatefulWindowsServiceManager services,
        IManifestHealthGate healthGate) =>
        new(
            [new WindowsServiceDeploymentEntrypointAdapter(services)],
            [new RecordingControlAdapter()],
            healthGate);

    private static DeploymentEntrypointTarget CreateTarget(
        string root,
        string componentId,
        string serviceName,
        bool allowCreate)
    {
        var componentRoot = Path.Combine(root, componentId);
        Directory.CreateDirectory(componentRoot);
        var executable = Path.Combine(componentRoot, "ServiceHost.exe");
        File.WriteAllText(executable, "fixture");
        var binaryPath = $"\"{executable}\" --component {componentId}";
        var specification = new WindowsServiceInstallSpec(
            serviceName,
            $"Sample {componentId}",
            binaryPath,
            "delayed",
            "local-system",
            "LocalSystem",
            [],
            3);
        return new DeploymentEntrypointTarget(
            "sample", "test", componentId, "windowsService", serviceName,
            executable, binaryPath, componentRoot, ["--component", componentId],
            AllowCreate: allowCreate,
            WindowsService: specification);
    }

    private static DeploymentActivationRequest CreateActivationRequest(string root, bool allowCreate)
    {
        var releasePath = Path.Combine(root, "release");
        foreach (var component in new[] { "engine", "converter", "gateway" })
        {
            var path = Path.Combine(releasePath, "package", component);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "ServiceHost.exe"), "fixture");
        }

        var project = JsonNode.Parse("""
        {
          "components": [
            { "id":"engine", "displayName":"Engine", "kind":"windowsService", "entrypoint":"engine-main", "dependsOn":[], "health":[], "service":{"startMode":"delayed","failureRestartLimit":3} },
            { "id":"converter", "displayName":"Converter", "kind":"windowsService", "entrypoint":"converter-main", "dependsOn":["engine"], "health":[], "service":{"startMode":"delayed","failureRestartLimit":3} },
            { "id":"gateway", "displayName":"Gateway", "kind":"windowsService", "entrypoint":"gateway-main", "dependsOn":["converter"], "health":[], "service":{"startMode":"delayed","failureRestartLimit":3} }
          ],
          "update": { "healthTimeoutSeconds": 5 }
        }
        """)!.AsObject();
        var release = JsonNode.Parse("""
        {
          "componentPayloads": [
            { "componentId":"engine", "entrypoint":"engine-main", "artifactId":"package", "path":"engine/ServiceHost.exe", "workingDirectory":"engine", "arguments":["--component","engine"] },
            { "componentId":"converter", "entrypoint":"converter-main", "artifactId":"package", "path":"converter/ServiceHost.exe", "workingDirectory":"converter", "arguments":["--component","converter"] },
            { "componentId":"gateway", "entrypoint":"gateway-main", "artifactId":"package", "path":"gateway/ServiceHost.exe", "workingDirectory":"gateway", "arguments":["--component","gateway"] }
          ]
        }
        """)!.AsObject();
        var binding = JsonNode.Parse($$"""
        {
          "roots": { "install": {{JsonSerializer.Serialize(Path.Combine(root, "install"))}} },
          "componentBindings": [
            { "componentId":"engine", "nativeName":"Company.Sample.Engine", "serviceAccountRef":"local-system" },
            { "componentId":"converter", "nativeName":"Company.Sample.Converter", "serviceAccountRef":"local-system" },
            { "componentId":"gateway", "nativeName":"Company.Sample.Gateway", "serviceAccountRef":"local-system" }
          ],
          "portBindings": []
        }
        """)!.AsObject();
        return new DeploymentActivationRequest("sample", "test", releasePath, project, release, binding, allowCreate);
    }

    private static DeploymentActivationRequest CreateWebQuizBotLikeRequest(string root)
    {
        var releasePath = Path.Combine(root, "release");
        Directory.CreateDirectory(Path.Combine(releasePath, "package", "api"));
        Directory.CreateDirectory(Path.Combine(releasePath, "package", "browser"));
        File.WriteAllText(Path.Combine(releasePath, "package", "api", "WebQuizBot.exe"), "fixture");
        File.WriteAllText(Path.Combine(releasePath, "package", "browser", "BrowserHost.exe"), "fixture");
        var project = JsonNode.Parse("""
        {
          "components": [
            { "id":"api", "displayName":"WebQuizBot API", "kind":"windowsService", "entrypoint":"api-main", "dependsOn":[], "health":[], "service":{"startMode":"delayed","failureRestartLimit":3} },
            { "id":"browser-host", "displayName":"Browser Host", "kind":"interactiveApp", "entrypoint":"browser-main", "dependsOn":["api"], "health":[], "interactive":{"executable":"BrowserHost.exe","workingDirectory":".","startPolicy":"userLogon"} }
          ],
          "update": { "healthTimeoutSeconds": 5 }
        }
        """)!.AsObject();
        var release = JsonNode.Parse("""
        {
          "componentPayloads": [
            { "componentId":"api", "entrypoint":"api-main", "artifactId":"package", "path":"api/WebQuizBot.exe", "workingDirectory":"api", "arguments":[] },
            { "componentId":"browser-host", "entrypoint":"browser-main", "artifactId":"package", "path":"browser/BrowserHost.exe", "workingDirectory":"browser", "arguments":[] }
          ]
        }
        """)!.AsObject();
        var binding = JsonNode.Parse($$"""
        {
          "roots": { "install": {{JsonSerializer.Serialize(Path.Combine(root, "install"))}} },
          "componentBindings": [
            { "componentId":"api", "nativeName":"WebQuizBot", "serviceAccountRef":"local-system" },
            { "componentId":"browser-host", "nativeName":"interactive-session", "serviceAccountRef":"interactive-user" }
          ],
          "portBindings": []
        }
        """)!.AsObject();
        return new DeploymentActivationRequest("webquizbot", "production", releasePath, project, release, binding, true);
    }

    private static WindowsServiceObservedState Observed(string serviceName, string executable) =>
        new(serviceName, serviceName, $"\"{executable}\"", false, "manual", "LocalSystem", [], 0);

    private static WindowsServiceObservedState FromTarget(DeploymentEntrypointTarget target)
    {
        var specification = target.WindowsService!;
        return new(
            specification.ServiceName,
            specification.DisplayName,
            specification.BinaryPath,
            false,
            specification.StartMode,
            specification.ServiceAccountName,
            specification.Dependencies,
            specification.FailureRestartLimit);
    }

    private sealed class StatefulWindowsServiceManager(
        IReadOnlyList<WindowsServiceObservedState> initial) : IWindowsServiceManager
    {
        private readonly Dictionary<string, WindowsServiceObservedState> _services =
            initial.ToDictionary(item => item.ServiceName, StringComparer.OrdinalIgnoreCase);

        public string? FailCreateServiceName { get; init; }
        public int CreateAttempts { get; private set; }
        public List<string> Events { get; } = [];
        public List<string> DeleteAttempts { get; } = [];
        public IReadOnlyList<WindowsServiceObservedState> Services => _services.Values.ToArray();

        public IReadOnlyList<WindowsServiceObservedState> FindExact(string serviceName) =>
            _services.TryGetValue(serviceName, out var service) ? [service] : [];

        public AdapterExecutionResult Create(WindowsServiceInstallSpec specification)
        {
            CreateAttempts++;
            if (string.Equals(FailCreateServiceName, specification.ServiceName, StringComparison.Ordinal))
            {
                Events.Add($"create-failed:{specification.ServiceName}");
                return new(false, "injected create failure");
            }
            if (_services.ContainsKey(specification.ServiceName))
                return new(false, "service already exists");
            _services.Add(specification.ServiceName, FromSpec(specification));
            Events.Add($"create:{specification.ServiceName}");
            return new(true, "created");
        }

        public AdapterExecutionResult ChangeBinaryPath(string serviceName, string binaryPath)
        {
            if (!_services.TryGetValue(serviceName, out var service))
                return new(false, "service missing");
            _services[serviceName] = service with { BinaryPath = binaryPath };
            Events.Add($"change:{serviceName}");
            return new(true, "changed");
        }

        public AdapterExecutionResult DeleteCreated(WindowsServiceInstallSpec specification)
        {
            DeleteAttempts.Add(specification.ServiceName);
            if (!_services.TryGetValue(specification.ServiceName, out var service))
                return new(true, "nothing created");
            if (!Matches(service, specification))
                return new(false, "identity changed");
            _services.Remove(specification.ServiceName);
            Events.Add($"delete:{specification.ServiceName}");
            return new(true, "deleted");
        }

        public void Replace(string serviceName, WindowsServiceObservedState replacement) =>
            _services[serviceName] = replacement;

        private static WindowsServiceObservedState FromSpec(WindowsServiceInstallSpec specification) =>
            new(
                specification.ServiceName,
                specification.DisplayName,
                specification.BinaryPath,
                false,
                specification.StartMode,
                specification.ServiceAccountName,
                specification.Dependencies,
                specification.FailureRestartLimit,
                specification.OwnershipMarker);

        private static bool Matches(WindowsServiceObservedState state, WindowsServiceInstallSpec specification) =>
            state.ServiceName == specification.ServiceName &&
            state.DisplayName == specification.DisplayName &&
            state.BinaryPath == specification.BinaryPath &&
            state.StartMode == specification.StartMode &&
            state.ServiceAccountName == specification.ServiceAccountName &&
            state.Dependencies.SequenceEqual(specification.Dependencies, StringComparer.OrdinalIgnoreCase) &&
            state.FailureRestartLimit == specification.FailureRestartLimit &&
            state.Description == specification.OwnershipMarker;
    }

    private sealed class RecordingControlAdapter : IComponentControlAdapter
    {
        public string Kind => "windowsService";

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterExecutionResult(true, action.ToString()));
    }

    private sealed class RecordingHealthGate(string? failComponent = null) : IManifestHealthGate
    {
        public Task<HealthGateResult> ProbeAsync(
            JsonObject projectManifest,
            JsonObject binding,
            string componentId,
            CancellationToken cancellationToken) =>
            componentId == failComponent
                ? Task.FromException<HealthGateResult>(new IOException("injected health failure"))
                : Task.FromResult(new HealthGateResult(true, "healthy"));
    }

    private sealed class TimelineEntrypointAdapter(string kind, List<string> events) : IDeploymentEntrypointAdapter
    {
        public string Kind => kind;

        public Task<DeploymentEntrypointCaptureResult> CaptureAsync(
            DeploymentEntrypointTarget target,
            CancellationToken cancellationToken)
        {
            events.Add($"capture:{target.ComponentId}");
            return Task.FromResult(new DeploymentEntrypointCaptureResult(
                true,
                new DeploymentEntrypointSnapshot(
                    target.ComponentId,
                    target.Kind,
                    target.NativeName,
                    string.Empty,
                    false,
                    ProjectId: target.ProjectId,
                    Environment: target.Environment,
                    ExecutablePath: target.ExecutablePath,
                    WorkingDirectory: target.WorkingDirectory,
                    Arguments: target.Arguments,
                    HadManagedState: false)));
        }

        public Task<AdapterExecutionResult> ApplyAsync(
            DeploymentEntrypointTarget target,
            DeploymentEntrypointSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"apply:{target.ComponentId}");
            return Task.FromResult(new AdapterExecutionResult(true, "applied"));
        }

        public Task<AdapterExecutionResult> RestoreAsync(
            DeploymentEntrypointSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"restore:{snapshot.ComponentId}");
            return Task.FromResult(new AdapterExecutionResult(true, "restored"));
        }
    }

    private sealed class TimelineControlAdapter(string kind, List<string> events) : IComponentControlAdapter
    {
        public string Kind => kind;

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken)
        {
            events.Add($"control:{action}:{target.ComponentId}");
            return Task.FromResult(new AdapterExecutionResult(true, "controlled"));
        }
    }

    private sealed class TimelineHealthGate(List<string> events, string? failComponent = null) : IManifestHealthGate
    {
        public Task<HealthGateResult> ProbeAsync(
            JsonObject projectManifest,
            JsonObject binding,
            string componentId,
            CancellationToken cancellationToken)
        {
            events.Add($"health:{componentId}");
            return componentId == failComponent
                ? Task.FromException<HealthGateResult>(new IOException("injected failure"))
                : Task.FromResult(new HealthGateResult(true, "healthy"));
        }
    }
}
