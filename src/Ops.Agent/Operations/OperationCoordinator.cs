using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Persistence;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Operations;

public sealed class OperationCoordinator
{
    private readonly AgentSnapshotCache _snapshotCache;
    private readonly IOpsStateStore _stateStore;
    private readonly OperationGate _gate;
    private readonly IReadOnlyDictionary<string, IComponentControlAdapter> _adapters;
    private readonly OpsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IComponentHealthGate _healthGate;
    private readonly ConcurrentDictionary<string, IdempotentOperation> _operations =
        new(StringComparer.Ordinal);

    public OperationCoordinator(
        AgentSnapshotCache snapshotCache,
        IOpsStateStore stateStore,
        OperationGate gate,
        IEnumerable<IComponentControlAdapter> adapters,
        IComponentHealthGate healthGate,
        IOptions<OpsOptions> options,
        JsonSerializerOptions jsonOptions)
    {
        _snapshotCache = snapshotCache;
        _stateStore = stateStore;
        _gate = gate;
        _options = options.Value;
        _jsonOptions = jsonOptions;
        _healthGate = healthGate;
        _adapters = adapters.ToDictionary(static adapter => adapter.Kind, StringComparer.Ordinal);
    }

    public Task<ComponentOperationResult> ExecuteAsync(
        ComponentOperationRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Task.FromResult(Rejected(request, "invalid_operation", validationError));
        }

        var fingerprint = JsonSerializer.Serialize(request with { IdempotencyKey = string.Empty }, _jsonOptions);
        var candidate = new IdempotentOperation(
            fingerprint,
            new Lazy<Task<ComponentOperationResult>>(
                () => ExecuteCoreAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _operations.GetOrAdd(request.IdempotencyKey, candidate);
        if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Task.FromResult(Rejected(
                request,
                "idempotency_conflict",
                "同一 IdempotencyKey 已用于不同请求"));
        }

        return operation.Execution.Value;
    }

    private async Task<ComponentOperationResult> ExecuteCoreAsync(
        ComponentOperationRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        if (!_options.EnableMutations)
        {
            return await AuditAndReturnAsync(
                Rejected(request, "mutations_disabled", "Agent 未启用变更操作", startedAt),
                cancellationToken);
        }

        var snapshot = _snapshotCache.Read();
        var project = snapshot.Projects?.Projects.SingleOrDefault(
            item =>
                string.Equals(item.ProjectId, request.ProjectId, StringComparison.Ordinal) &&
                string.Equals(item.Environment, request.Environment, StringComparison.Ordinal));
        if (project is null)
        {
            return await AuditAndReturnAsync(
                Rejected(request, "project_not_found", "未找到唯一项目环境", startedAt),
                cancellationToken);
        }

        if (project.Status == ProjectBindingStatus.Conflict || project.Generation != request.ExpectedGeneration)
        {
            return await AuditAndReturnAsync(
                Rejected(request, "generation_or_ownership_conflict", "项目归属冲突或 InstalledState generation 已变化", startedAt),
                cancellationToken);
        }

        var plan = await BuildPlanAsync(request, project, snapshot.Catalog, cancellationToken);
        if (plan.Error is not null)
        {
            return await AuditAndReturnAsync(
                Rejected(request, plan.Error.Value.Code, plan.Error.Value.Detail, startedAt),
                cancellationToken);
        }

        using var lease = _gate.TryAcquire(plan.Steps.Select(static step => step.Target.NativeId));
        if (lease is null)
        {
            return await AuditAndReturnAsync(
                Rejected(request, "resource_busy", "目标资源正在执行其他操作", startedAt),
                cancellationToken);
        }

        var completed = new List<ComponentOperationStep>();
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_adapters.TryGetValue(step.Target.Kind, out var adapter))
            {
                return await AuditAndReturnAsync(
                    Failed(request, startedAt, completed, "adapter_missing", $"缺少 {step.Target.Kind} 控制适配器"),
                    cancellationToken);
            }

            var result = await adapter.ExecuteAsync(step.Target, step.Action, cancellationToken);
            completed.Add(new ComponentOperationStep(
                step.Target.ComponentId,
                adapter.GetType().Name,
                step.Action.ToString(),
                result.Success ? "succeeded" : "failed",
                result.Detail));
            if (!result.Success)
            {
                return await AuditAndReturnAsync(
                    Failed(request, startedAt, completed, "adapter_failed", result.Detail),
                    cancellationToken);
            }

            if (step.Action is ComponentOperationAction.Start or ComponentOperationAction.Restart)
            {
                var health = await _healthGate.ProbeAsync(
                    request.ProjectId,
                    request.Environment,
                    step.Target.ComponentId,
                    cancellationToken);
                completed.Add(new ComponentOperationStep(
                    step.Target.ComponentId,
                    "DeclaredHealthGate",
                    "Health",
                    health.Success ? "succeeded" : "failed",
                    health.Detail));
                if (!health.Success)
                {
                    return await AuditAndReturnAsync(
                        Failed(request, startedAt, completed, "health_gate_failed", health.Detail),
                        cancellationToken);
                }
            }
        }

        return await AuditAndReturnAsync(
            new ComponentOperationResult(
                request.OperationId,
                request.ProjectId,
                request.Environment,
                request.ComponentId,
                request.Action,
                OperationOutcome.Succeeded,
                startedAt,
                DateTimeOffset.UtcNow,
                completed),
            cancellationToken);
    }

    private static async Task<OperationPlan> BuildPlanAsync(
        ComponentOperationRequest request,
        ProjectRuntimeView project,
        ManifestCatalogSnapshot? catalog,
        CancellationToken cancellationToken)
    {
        var target = project.Components.SingleOrDefault(
            component => string.Equals(component.ComponentId, request.ComponentId, StringComparison.Ordinal));
        if (target is null)
        {
            return OperationPlan.Fail("component_not_found", "未找到唯一组件");
        }

        if (target.Ownership != ComponentOwnershipStatus.Owned)
        {
            return OperationPlan.Fail("ownership_not_proven", "组件未通过精确归属校验");
        }

        var orderedIds = new List<string>();
        if (request.Action is ComponentOperationAction.Start or ComponentOperationAction.Restart)
        {
            var manifestEntry = catalog?.Entries.SingleOrDefault(
                entry =>
                    entry.IsValid &&
                    entry.ManifestKind == "ProjectManifest" &&
                    string.Equals(entry.ProjectId, request.ProjectId, StringComparison.Ordinal));
            if (manifestEntry is null)
            {
                return OperationPlan.Fail("manifest_not_unique", "无法取得唯一 ProjectManifest");
            }

            var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestEntry.Path, cancellationToken)) as JsonObject;
            var componentNodes = root?["components"]?.AsArray().OfType<JsonObject>()
                .ToDictionary(static item => item["id"]!.GetValue<string>(), StringComparer.Ordinal);
            if (componentNodes is null || !componentNodes.ContainsKey(request.ComponentId))
            {
                return OperationPlan.Fail("component_not_found", "ProjectManifest 中不存在该组件");
            }

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Visit(string id)
            {
                if (!componentNodes.TryGetValue(id, out var node) || !visiting.Add(id))
                {
                    return false;
                }

                if (visited.Contains(id))
                {
                    visiting.Remove(id);
                    return true;
                }

                foreach (var dependency in node["dependsOn"]?.AsArray().Select(static value => value!.GetValue<string>()) ?? [])
                {
                    if (!Visit(dependency))
                    {
                        return false;
                    }
                }

                visiting.Remove(id);
                visited.Add(id);
                orderedIds.Add(id);
                return true;
            }

            if (!Visit(request.ComponentId))
            {
                return OperationPlan.Fail("dependency_invalid", "组件依赖缺失或形成环");
            }
        }
        else
        {
            orderedIds.Add(request.ComponentId);
        }

        var steps = new List<PlannedStep>();
        foreach (var id in orderedIds)
        {
            var component = project.Components.SingleOrDefault(item => item.ComponentId == id);
            if (component is null || component.Ownership != ComponentOwnershipStatus.Owned)
            {
                return OperationPlan.Fail("dependency_ownership_not_proven", $"依赖组件 {id} 未通过精确归属校验");
            }

            var action = id == request.ComponentId ? request.Action : ComponentOperationAction.Start;
            steps.Add(new PlannedStep(ToTarget(project, component), action));
        }

        return new OperationPlan(steps, null);
    }

    private static ComponentControlTarget ToTarget(
        ProjectRuntimeView project,
        ProjectComponentRuntimeView component)
    {
        int? pmId = null;
        if (component.Kind == "pm2Legacy" &&
            component.InstalledNativeId?.StartsWith("pm_id:", StringComparison.Ordinal) == true &&
            int.TryParse(component.InstalledNativeId.AsSpan(6), out var parsed))
        {
            pmId = parsed;
        }

        return new ComponentControlTarget(
            project.ProjectId,
            project.Environment,
            component.ComponentId,
            component.Kind,
            component.Kind == "pm2Legacy" ? component.ExpectedNativeId : component.InstalledNativeId!,
            pmId);
    }

    private async Task<ComponentOperationResult> AuditAndReturnAsync(
        ComponentOperationResult result,
        CancellationToken cancellationToken)
    {
        await _stateStore.AppendAuditEventAsync(
            new AuditEvent(
                Guid.CreateVersion7().ToString(),
                DateTimeOffset.UtcNow,
                "operation",
                result.Action.ToString(),
                result.Outcome.ToString(),
                $"{result.ProjectId}/{result.Environment}/{result.ComponentId}: {result.ErrorCode ?? result.Detail}"),
            cancellationToken);
        return result;
    }

    private static string? ValidateRequest(ComponentOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.Environment) ||
            string.IsNullOrWhiteSpace(request.ComponentId) ||
            request.ExpectedGeneration < 1)
        {
            return "操作标识、项目、环境、组件和 generation 必须完整且在范围内";
        }

        return null;
    }

    private static ComponentOperationResult Rejected(
        ComponentOperationRequest request,
        string code,
        string detail,
        DateTimeOffset? startedAt = null) =>
        new(
            request.OperationId,
            request.ProjectId,
            request.Environment,
            request.ComponentId,
            request.Action,
            OperationOutcome.Rejected,
            startedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            code,
            detail);

    private static ComponentOperationResult Failed(
        ComponentOperationRequest request,
        DateTimeOffset startedAt,
        IReadOnlyList<ComponentOperationStep> steps,
        string code,
        string? detail) =>
        new(
            request.OperationId,
            request.ProjectId,
            request.Environment,
            request.ComponentId,
            request.Action,
            OperationOutcome.Failed,
            startedAt,
            DateTimeOffset.UtcNow,
            steps,
            code,
            detail);

    private sealed record IdempotentOperation(
        string Fingerprint,
        Lazy<Task<ComponentOperationResult>> Execution);

    private sealed record PlannedStep(
        ComponentControlTarget Target,
        ComponentOperationAction Action);

    private sealed record OperationPlan(
        IReadOnlyList<PlannedStep> Steps,
        (string Code, string Detail)? Error)
    {
        public static OperationPlan Fail(string code, string detail) => new([], (code, detail));
    }
}
