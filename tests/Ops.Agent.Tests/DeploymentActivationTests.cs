using System.Text.Json.Nodes;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Operations;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Tests;

public sealed class DeploymentActivationTests
{
    [Fact]
    public async Task Plan_RejectsUnsupportedNativeKindBeforeMutation()
    {
        using var directory = new TestDirectory();
        var events = new List<string>();
        var activator = CreateActivator(events);
        var request = Request(
            directory.FullPath,
            """
            [{ "id": "web", "kind": "iisSite", "entrypoint": "web-main", "dependsOn": [], "health": [] }]
            """,
            """
            [{ "componentId": "web", "entrypoint": "web-main", "artifactId": "package", "path": "web" }]
            """,
            """
            [{ "componentId": "web", "nativeName": "Company.Sample.Web" }]
            """,
            "[]");

        var result = await activator.PlanAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("尚无生产激活适配器", result.Detail, StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Plan_ReadOnlyPreflightsExistingNativeResource()
    {
        using var directory = new TestDirectory();
        var events = new List<string>();
        var activator = CreateActivator(events);
        var request = Request(
            directory.FullPath,
            """
            [{ "id": "api", "kind": "windowsService", "entrypoint": "api-main", "dependsOn": [], "health": [] }]
            """,
            """
            [{ "componentId": "api", "entrypoint": "api-main", "artifactId": "package", "path": "api/Sample.Api.exe" }]
            """,
            """
            [{ "componentId": "api", "nativeName": "Company.Sample.Api" }]
            """,
            "[]");

        var result = await activator.PlanAsync(request, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(["capture:api"], events);
    }

    [Fact]
    public async Task Activate_PreflightsThenSwitchesAndStartsInDependencyOrder()
    {
        using var directory = new TestDirectory();
        CreatePayload(directory.FullPath, "package", "api/Sample.Api.exe");
        CreatePayload(directory.FullPath, "package", "worker/Sample.Worker.exe");
        var events = new List<string>();
        var activator = CreateActivator(events);
        var request = Request(
            directory.FullPath,
            """
            [
              {
                "id": "api", "kind": "windowsService", "entrypoint": "api-main",
                "dependsOn": [], "health": []
              },
              {
                "id": "worker", "kind": "windowsService", "entrypoint": "worker-main",
                "dependsOn": ["api"], "health": []
              }
            ]
            """,
            """
            [
              {
                "componentId": "api", "entrypoint": "api-main", "artifactId": "package",
                "path": "api/Sample.Api.exe", "workingDirectory": "api",
                "arguments": ["--port", "${PORT_API_HTTP}"]
              },
              {
                "componentId": "worker", "entrypoint": "worker-main", "artifactId": "package",
                "path": "worker/Sample.Worker.exe", "workingDirectory": "worker"
              }
            ]
            """,
            """
            [
              { "componentId": "api", "nativeName": "Company.Sample.Api" },
              { "componentId": "worker", "nativeName": "Company.Sample.Worker" }
            ]
            """,
            """
            [{
              "portId": "api-http", "componentId": "api", "protocol": "tcp",
              "address": "127.0.0.1", "port": 19201
            }]
            """);

        var result = await activator.ActivateAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        Assert.NotNull(result.Rollback);
        Assert.Equal(
            [
                "capture:api",
                "capture:worker",
                "control:Stop:worker",
                "control:Stop:api",
                "apply:api:19201",
                "apply:worker:",
                "control:Start:api",
                "health:api",
                "control:Start:worker",
                "health:worker"
            ],
            events);

        var restored = await result.Rollback!.RestoreAsync(CancellationToken.None);

        Assert.True(restored.Success, restored.Detail);
        Assert.Equal(
            [
                "control:Stop:worker",
                "control:Stop:api",
                "restore:worker",
                "restore:api",
                "control:Start:api",
                "health:api",
                "control:Start:worker",
                "health:worker"
            ],
            events.Skip(10).ToArray());
    }

    [Fact]
    public async Task Activate_StartFailureRestoresOldEntrypointsAndOriginalRunningState()
    {
        using var directory = new TestDirectory();
        CreatePayload(directory.FullPath, "package", "api/Sample.Api.exe");
        var events = new List<string>();
        var activator = CreateActivator(events, failStartComponent: "api");
        var request = Request(
            directory.FullPath,
            """
            [{
              "id": "api", "kind": "windowsService", "entrypoint": "api-main",
              "dependsOn": [], "health": []
            }]
            """,
            """
            [{
              "componentId": "api", "entrypoint": "api-main", "artifactId": "package",
              "path": "api/Sample.Api.exe"
            }]
            """,
            """
            [{ "componentId": "api", "nativeName": "Company.Sample.Api" }]
            """,
            "[]");

        var result = await activator.ActivateAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("失败恢复成功", result.Detail, StringComparison.Ordinal);
        Assert.Equal(
            [
                "capture:api",
                "control:Stop:api",
                "apply:api:",
                "control:Start:api",
                "control:Stop:api",
                "restore:api",
                "control:Start:api",
                "health:api"
            ],
            events);
    }

    [Fact]
    public async Task Activate_UnexpectedAdapterExceptionStillRestoresOldEntrypoint()
    {
        using var directory = new TestDirectory();
        CreatePayload(directory.FullPath, "package", "api/Sample.Api.exe");
        var events = new List<string>();
        var activator = CreateActivator(events, throwApplyComponent: "api");
        var request = Request(
            directory.FullPath,
            """
            [{
              "id": "api", "kind": "windowsService", "entrypoint": "api-main",
              "dependsOn": [], "health": []
            }]
            """,
            """
            [{
              "componentId": "api", "entrypoint": "api-main", "artifactId": "package",
              "path": "api/Sample.Api.exe"
            }]
            """,
            """
            [{ "componentId": "api", "nativeName": "Company.Sample.Api" }]
            """,
            "[]");

        var result = await activator.ActivateAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("激活异常", result.Detail, StringComparison.Ordinal);
        Assert.Contains("旧入口恢复成功", result.Detail, StringComparison.Ordinal);
        Assert.Equal(
            [
                "capture:api",
                "control:Stop:api",
                "apply:api:",
                "control:Stop:api",
                "restore:api",
                "control:Start:api",
                "health:api"
            ],
            events);
    }

    [Fact]
    public async Task Plan_RejectsUnknownArgumentPlaceholder()
    {
        using var directory = new TestDirectory();
        var events = new List<string>();
        var activator = CreateActivator(events);
        var request = Request(
            directory.FullPath,
            """
            [{ "id": "api", "kind": "windowsService", "entrypoint": "api-main", "dependsOn": [], "health": [] }]
            """,
            """
            [{
              "componentId": "api", "entrypoint": "api-main", "artifactId": "package",
              "path": "api/Sample.Api.exe", "arguments": ["${SECRET_VALUE}"]
            }]
            """,
            """
            [{ "componentId": "api", "nativeName": "Company.Sample.Api" }]
            """,
            "[]");

        var result = await activator.PlanAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("未知或未绑定占位符", result.Detail, StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Activate_MixesServiceAndInteractiveAppWithoutProjectSpecificCode()
    {
        using var directory = new TestDirectory();
        CreatePayload(directory.FullPath, "package", "api/Api.exe");
        CreatePayload(directory.FullPath, "package", "host/Host.exe");
        var events = new List<string>();
        var targets = new List<DeploymentEntrypointTarget>();
        var activator = CreateActivator(events, capturedTargets: targets, kinds: ["windowsService", "interactiveApp"]);
        var request = Request(
            directory.FullPath,
            """
            [
              { "id": "api", "kind": "windowsService", "entrypoint": "api-main", "dependsOn": [], "health": [] },
              { "id": "host", "kind": "interactiveApp", "entrypoint": "host-main", "dependsOn": ["api"], "health": [] }
            ]
            """,
            """
            [
              {
                "componentId": "api", "entrypoint": "api-main", "artifactId": "package",
                "path": "api/Api.exe", "workingDirectory": "api",
                "arguments": ["--data-dir", "${ROOT_DATA}", "--port", "${PORT_API_HTTP}"]
              },
              {
                "componentId": "host", "entrypoint": "host-main", "artifactId": "package",
                "path": "host/Host.exe", "workingDirectory": "host",
                "arguments": ["--logs-dir", "${ROOT_LOGS}"]
              }
            ]
            """,
            """
            [
              { "componentId": "api", "nativeName": "Sample.Api" },
              { "componentId": "host", "nativeName": "interactive-session" }
            ]
            """,
            """
            [{ "portId": "api-http", "componentId": "api", "protocol": "tcp", "address": "127.0.0.1", "port": 19201 }]
            """);

        var result = await activator.ActivateAsync(request, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(
            [
                "capture:api", "capture:host",
                "control:Stop:host", "control:Stop:api",
                "apply:api:19201", "apply:host:",
                "control:Start:api", "health:api",
                "control:Start:host", "health:host"
            ],
            events);
        Assert.Equal(
            ["--data-dir", @"C:\CompanyOpsTests\sample-data", "--port", "19201"],
            targets.Single(target => target.ComponentId == "api").Arguments);
        Assert.Equal(
            ["--logs-dir", @"C:\CompanyOpsTests\sample-logs"],
            targets.Single(target => target.ComponentId == "host").Arguments);
    }

    private static NativeDeploymentActivator CreateActivator(
        List<string> events,
        string? failStartComponent = null,
        string? throwApplyComponent = null,
        List<DeploymentEntrypointTarget>? capturedTargets = null,
        IReadOnlyList<string>? kinds = null) =>
        new(
            (kinds ?? ["windowsService"])
                .Select(kind => new FakeEntrypointAdapter(kind, events, throwApplyComponent, capturedTargets)),
            (kinds ?? ["windowsService"])
                .Select(kind => new FakeControlAdapter(kind, events, failStartComponent)),
            new RecordingHealthGate(events));

    private static DeploymentActivationRequest Request(
        string releasePath,
        string components,
        string payloads,
        string componentBindings,
        string portBindings)
    {
        var project = JsonNode.Parse(
            $$"""
            {
              "components": {{components}},
              "update": { "strategy": "stopStart", "rollbackOnFailure": true, "healthTimeoutSeconds": 5 }
            }
            """)!.AsObject();
        var release = JsonNode.Parse(
            $$"""
            { "componentPayloads": {{payloads}} }
            """)!.AsObject();
        var binding = JsonNode.Parse(
            $$"""
            {
              "roots": {
                "install": "C:\\CompanyOpsTests\\sample",
                "data": "C:\\CompanyOpsTests\\sample-data",
                "logs": "C:\\CompanyOpsTests\\sample-logs"
              },
              "componentBindings": {{componentBindings}},
              "portBindings": {{portBindings}}
            }
            """)!.AsObject();
        return new DeploymentActivationRequest("sample", "test", releasePath, project, release, binding);
    }

    private static void CreatePayload(string releasePath, string artifactId, string relativePath)
    {
        var path = Path.Combine(releasePath, artifactId, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test binary");
    }

    private sealed class FakeEntrypointAdapter(
        string kind,
        List<string> events,
        string? throwApplyComponent,
        List<DeploymentEntrypointTarget>? capturedTargets) : IDeploymentEntrypointAdapter
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
                    $"old-{target.ComponentId}.exe",
                    WasRunning: true)));
        }

        public Task<AdapterExecutionResult> ApplyAsync(
            DeploymentEntrypointTarget target,
            DeploymentEntrypointSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"apply:{target.ComponentId}:{(target.BinaryPath.Contains("19201", StringComparison.Ordinal) ? "19201" : string.Empty)}");
            capturedTargets?.Add(target);
            if (target.ComponentId == throwApplyComponent)
            {
                throw new InvalidOperationException("injected adapter exception");
            }

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

    private sealed class FakeControlAdapter(
        string kind,
        List<string> events,
        string? failStartComponent) : IComponentControlAdapter
    {
        private bool _failed;

        public string Kind => kind;

        public Task<AdapterExecutionResult> ExecuteAsync(
            ComponentControlTarget target,
            ComponentOperationAction action,
            CancellationToken cancellationToken)
        {
            events.Add($"control:{action}:{target.ComponentId}");
            if (!_failed && action == ComponentOperationAction.Start && target.ComponentId == failStartComponent)
            {
                _failed = true;
                return Task.FromResult(new AdapterExecutionResult(false, "injected start failure"));
            }

            return Task.FromResult(new AdapterExecutionResult(true, "controlled"));
        }
    }

    private sealed class RecordingHealthGate(List<string> events) : IManifestHealthGate
    {
        public Task<HealthGateResult> ProbeAsync(
            JsonObject projectManifest,
            JsonObject binding,
            string componentId,
            CancellationToken cancellationToken)
        {
            events.Add($"health:{componentId}");
            return Task.FromResult(new HealthGateResult(true, "healthy"));
        }
    }
}
