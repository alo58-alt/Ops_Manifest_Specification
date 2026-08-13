using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Projects;
using CompanyOps.Contracts;
using Json.Schema;

namespace CompanyOps.Agent.Onboarding;

public sealed class ExistingProjectOnboardingService(
    OpsPathResolver pathResolver,
    IManifestCatalog manifestCatalog,
    AgentSnapshotCache snapshotCache,
    IProjectRegistry projectRegistry,
    IManifestHealthGate healthGate,
    IOpsStateStore stateStore,
    OperationGate operationGate,
    JsonSerializerOptions jsonOptions)
{
    private const long MaximumProjectManifestBytes = 4 * 1024 * 1024;
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();

    public async Task<ExistingProjectOnboardingResult> ExecuteAsync(
        ExistingProjectOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await CreatePlanAsync(request, cancellationToken);
        if (request.Action == ExistingProjectOnboardingAction.Plan)
        {
            return plan.Result;
        }
        if (!plan.Result.CanApply)
        {
            return plan.Result with { Action = ExistingProjectOnboardingAction.Apply };
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedPlanToken) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.ExpectedPlanToken),
                Encoding.UTF8.GetBytes(plan.Result.PlanToken ?? string.Empty)))
        {
            return plan.Result with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                Outcome = OperationOutcome.Rejected,
                CanApply = false,
                ErrorCode = "onboarding_plan_changed",
                Detail = "项目材料或主机匹配结果已变化，请重新检查后再确认接入。",
                Problems = plan.Result.Problems.Append("预检令牌不一致").ToArray()
            };
        }

        using var lease = operationGate.TryAcquire(
            ["onboarding", $"project:{plan.ProjectId}:{plan.Environment}"]);
        if (lease is null)
        {
            return plan.Result with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                Outcome = OperationOutcome.Rejected,
                CanApply = false,
                ErrorCode = "onboarding_busy",
                Detail = "当前有另一个接入或项目操作正在执行。"
            };
        }

        ManifestWriteResult? projectWrite = null;
        ManifestWriteResult? bindingWrite = null;
        var projectExistedBefore = File.Exists(plan.ProjectDestination);
        var bindingExistedBefore = File.Exists(plan.BindingDestination);
        try
        {
            Directory.CreateDirectory(_paths.ManifestDirectory);
            projectWrite = await WriteNewOrReplaceAsync(
                plan.ProjectDestination,
                plan.ProjectManifestJson,
                plan.ExistingProjectManifestJson,
                cancellationToken);
            bindingWrite = await WriteNewOrReplaceAsync(
                plan.BindingDestination,
                plan.BindingJson,
                plan.ExistingBindingJson,
                cancellationToken);

            var catalog = await manifestCatalog.InspectAsync(cancellationToken);
            var invalidNewFiles = catalog.Entries.Where(entry =>
                (string.Equals(entry.Path, plan.ProjectDestination, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(entry.Path, plan.BindingDestination, StringComparison.OrdinalIgnoreCase)) &&
                !entry.IsValid).ToArray();
            if (invalidNewFiles.Length > 0)
            {
                throw new InvalidDataException(string.Join(
                    "；",
                    invalidNewFiles.SelectMany(entry => entry.Errors)));
            }

            var inventory = snapshotCache.Read().Inventory
                ?? throw new InvalidOperationException("Agent 首次主机盘点尚未完成");
            var projects = await projectRegistry.BuildAsync(catalog, inventory, cancellationToken);
            snapshotCache.Update(inventory, catalog, projects);
            var project = projects.Projects.SingleOrDefault(item =>
                string.Equals(item.ProjectId, plan.ProjectId, StringComparison.Ordinal) &&
                string.Equals(item.Environment, plan.Environment, StringComparison.Ordinal));
            if (project is null || project.Status == ProjectBindingStatus.Conflict)
            {
                throw new InvalidDataException(
                    project is null
                        ? "导入后未形成项目视图"
                        : string.Join("；", project.Problems));
            }

            var health = new List<OnboardingHealthResult>();
            foreach (var component in plan.ProjectManifest["components"]!.AsArray().OfType<JsonObject>())
            {
                var componentId = component["id"]!.GetValue<string>();
                if (component["kind"]?.GetValue<string>() == "interactiveApp")
                {
                    health.Add(new OnboardingHealthResult(
                        componentId,
                        true,
                        "交互程序已完成声明和用户会话绑定；健康探针将在 Session Agent 接管后执行"));
                    continue;
                }
                var result = await healthGate.ProbeAsync(
                    plan.ProjectManifest,
                    plan.Binding,
                    componentId,
                    cancellationToken);
                health.Add(new OnboardingHealthResult(componentId, result.Success, result.Detail));
            }

            await stateStore.AppendAuditEventAsync(
                new AuditEvent(
                    Guid.CreateVersion7().ToString(),
                    DateTimeOffset.UtcNow,
                    "onboarding",
                    "apply-existing-project",
                    "succeeded",
                    $"{plan.ProjectId}/{plan.Environment} 从 {plan.ProjectRoot} 完成 L1 只读接入；未启停或修改业务服务。"),
                cancellationToken);

            return plan.Result with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                Outcome = OperationOutcome.Succeeded,
                AlreadyOnboarded = projectExistedBefore && bindingExistedBefore,
                Health = health,
                Steps =
                [
                    "ProjectManifest 已导入 CompanyOps 清单目录",
                    "EnvironmentBinding 已由当前主机生成并通过契约校验",
                    "项目视图已刷新；未创建 InstalledState",
                    "interactiveApp 如存在，已绑定当前 Console 用户会话，但未启动程序",
                    "未启动、停止、重启或修改任何业务服务"
                ],
                Detail = health.All(item => item.Success)
                    ? "L1 只读接入完成，声明式健康探针全部通过。"
                    : "L1 只读接入完成，但至少一个健康探针未通过，请查看结果。"
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (bindingWrite is not null)
            {
                TryRollbackWrite(plan.BindingDestination, bindingWrite);
            }
            if (projectWrite is not null)
            {
                TryRollbackWrite(plan.ProjectDestination, projectWrite);
            }

            await TryAuditFailureAsync(plan, exception.Message, cancellationToken);
            return plan.Result with
            {
                Action = ExistingProjectOnboardingAction.Apply,
                Outcome = OperationOutcome.Failed,
                CanApply = false,
                ErrorCode = "onboarding_apply_failed",
                Detail = $"接入失败，已撤销本次新写入的清单：{exception.Message}",
                Problems = plan.Result.Problems.Append(exception.Message).ToArray()
            };
        }
    }

    private async Task<OnboardingPlan> CreatePlanAsync(
        ExistingProjectOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var steps = new List<string>();
        var components = new List<OnboardingComponentProposal>();
        var ports = new List<OnboardingPortProposal>();
        var environment = NormalizeEnvironment(request.Environment, problems);
        var projectRoot = ResolveLocalDirectory(request.ProjectRoot, "项目目录", problems);
        var dataRoot = ResolveOptionalRoot(request.DataRoot, projectRoot, "数据目录", problems);
        var logsRoot = ResolveOptionalRoot(request.LogsRoot, projectRoot, "日志目录", problems);
        var hostId = _paths.HostId;
        JsonObject? manifest = null;
        string manifestJson = string.Empty;
        string? projectId = null;
        string? displayName = null;

        if (projectRoot is not null)
        {
            var sourcePath = Path.Combine(projectRoot, "ops", "project-manifest.json");
            var readmePath = Path.Combine(projectRoot, "ops", "README.md");
            if (!File.Exists(readmePath))
            {
                problems.Add($"项目缺少 ops\\README.md：{readmePath}");
            }
            if (!File.Exists(sourcePath))
            {
                problems.Add($"项目缺少 ops\\project-manifest.json：{sourcePath}");
            }
            else
            {
                try
                {
                    var file = new FileInfo(sourcePath);
                    if (file.Length > MaximumProjectManifestBytes)
                    {
                        problems.Add("ProjectManifest 超过 4 MiB 限制");
                    }
                    else
                    {
                        manifestJson = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                        manifest = JsonNode.Parse(
                            manifestJson,
                            documentOptions: new JsonDocumentOptions
                            {
                                AllowTrailingCommas = false,
                                CommentHandling = JsonCommentHandling.Disallow,
                                MaxDepth = 100
                            }) as JsonObject;
                        if (manifest is null)
                        {
                            problems.Add("ProjectManifest 根节点必须是 JSON object");
                        }
                        else
                        {
                            ValidateProjectManifest(manifest, problems);
                            projectId = manifest["metadata"]?["id"]?.GetValue<string>();
                            displayName = manifest["metadata"]?["displayName"]?.GetValue<string>();
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    problems.Add($"ProjectManifest 读取失败：{exception.Message}");
                }
            }
        }

        var inventory = snapshotCache.Read().Inventory;
        if (inventory is null)
        {
            problems.Add("Agent 首次主机盘点尚未完成");
        }

        JsonObject? binding = null;
        string? existingBindingJson = null;
        string? existingProjectManifestJson = null;
        if (manifest is not null && projectId is not null && projectRoot is not null && dataRoot is not null && logsRoot is not null)
        {
            var componentBindings = new JsonArray();
            foreach (var component in manifest["components"]!.AsArray().OfType<JsonObject>())
            {
                var componentId = component["id"]!.GetValue<string>();
                var componentDisplayName = component["displayName"]!.GetValue<string>();
                var kind = component["kind"]!.GetValue<string>();
                var proposal = ResolveNativeResource(
                    componentId,
                    componentDisplayName,
                    kind,
                    projectId,
                    displayName ?? projectId,
                    projectRoot,
                    request.NativeNames,
                    inventory);
                components.Add(proposal);
                if (kind == "interactiveApp")
                {
                    if (!TryBuildInteractiveBinding(
                            component,
                            componentId,
                            projectRoot,
                            out var interactiveNativeName,
                            out var interactiveProblem))
                    {
                        problems.Add(interactiveProblem!);
                        continue;
                    }
                    proposal = proposal with
                    {
                        NativeName = interactiveNativeName,
                        RequiresInput = false
                    };
                    components[^1] = proposal;
                }
                if (proposal.RequiresInput || proposal.NativeName is null)
                {
                    problems.Add(kind == "pm2Legacy"
                        ? $"组件 {componentId} 是遗留 PM2；必须先配置 owner Bridge，当前通用向导不会猜测 PM2 daemon 归属"
                        : $"组件 {componentId} 无法唯一匹配主机原生资源，请填写精确原生名称");
                    continue;
                }

                componentBindings.Add(new JsonObject
                {
                    ["componentId"] = componentId,
                    ["serviceAccountRef"] = "existing-service-account",
                    ["nativeName"] = proposal.NativeName
                });
            }

            var portBindings = new JsonArray();
            foreach (var port in manifest["ports"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var portId = port["id"]!.GetValue<string>();
                var componentId = port["componentId"]!.GetValue<string>();
                var protocol = port["protocol"]!.GetValue<string>();
                var exposure = port["exposure"]!.GetValue<string>();
                var requestedPort = request.Ports?.GetValueOrDefault(portId);
                var resolvedPort = requestedPort ?? port["preferredPort"]?.GetValue<int>();
                var requiresInput = resolvedPort is null or < 1 or > 65535;
                var address = exposure == "lan" ? "0.0.0.0" : "127.0.0.1";
                ports.Add(new OnboardingPortProposal(
                    portId,
                    componentId,
                    protocol,
                    address,
                    resolvedPort,
                    requiresInput));
                if (requiresInput)
                {
                    problems.Add($"端口 {portId} 没有可用的固定端口，请填写当前服务器实际端口");
                    continue;
                }

                portBindings.Add(new JsonObject
                {
                    ["portId"] = portId,
                    ["componentId"] = componentId,
                    ["protocol"] = protocol,
                    ["address"] = address,
                    ["port"] = resolvedPort
                });
            }

            var requiredSettings = manifest["configuration"]?.AsArray().OfType<JsonObject>()
                .Where(item => item["required"]?.GetValue<bool>() == true)
                .Select(item => item["key"]?.GetValue<string>())
                .Where(value => value is not null)
                .ToArray() ?? [];
            if (requiredSettings.Length > 0)
            {
                problems.Add($"项目存在必填配置，通用 L1 向导不会猜值：{string.Join(", ", requiredSettings)}");
            }

            binding = new JsonObject
            {
                ["$schema"] = "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/environment-binding.schema.json",
                ["apiVersion"] = "ops.company/v1",
                ["manifestKind"] = "EnvironmentBinding",
                ["metadata"] = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["environment"] = environment,
                    ["hostId"] = hostId,
                    ["revision"] = 1
                },
                ["roots"] = new JsonObject
                {
                    ["install"] = projectRoot,
                    ["data"] = dataRoot,
                    ["logs"] = logsRoot
                },
                ["componentBindings"] = componentBindings,
                ["portBindings"] = portBindings,
                ["settings"] = new JsonArray()
            };

            if (manifest["components"]!.AsArray().OfType<JsonObject>()
                .Any(static component => component["kind"]?.GetValue<string>() == "interactiveApp"))
            {
                var ownerSid = request.InteractiveOwnerSid;
                if (ownerSid is null || !System.Text.RegularExpressions.Regex.IsMatch(ownerSid, @"^S-1-(?:[0-9]+-)+[0-9]+$"))
                {
                    problems.Add("无法从当前 Windows 登录身份取得用户 SID，不能建立交互会话绑定");
                }
                else
                {
                    binding["interactiveSession"] = new JsonObject
                    {
                        ["ownerSid"] = ownerSid,
                        ["snapshotFileName"] = InteractiveSessionProtocol.SnapshotFileName(projectId, environment, ownerSid),
                        ["controlPipeName"] = InteractiveSessionProtocol.PipeName(ownerSid),
                        ["maxAgeSeconds"] = 30
                    };
                }
            }

            if (problems.Count == 0 && components.All(item => !item.RequiresInput) && ports.All(item => !item.RequiresInput))
            {
                var existingDocuments = await ValidateHostConflictsAsync(
                    projectId,
                    environment,
                    projectRoot,
                    manifest,
                    components,
                    binding,
                    problems,
                    cancellationToken);
                existingBindingJson = existingDocuments.BindingJson;
                existingProjectManifestJson = existingDocuments.ProjectManifestJson;
                ValidateBinding(binding, problems);
            }
        }

        var safeProjectId = projectId is not null && IsSafeSegment(projectId) ? projectId : "invalid";
        var safeEnvironment = IsSafeSegment(environment) ? environment : "invalid";
        var safeHostId = IsSafeSegment(hostId) ? hostId : "invalid";
        var projectDestination = Path.Combine(_paths.ManifestDirectory, $"{safeProjectId}.project-manifest.json");
        var bindingDestination = Path.Combine(
            _paths.ManifestDirectory,
            $"{safeProjectId}.{safeEnvironment}.{safeHostId}.binding.json");
        var bindingJson = binding?.ToJsonString(jsonOptions) ?? string.Empty;
        var canApply = problems.Count == 0 && manifest is not null && binding is not null;
        var planToken = canApply
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                manifestJson + "\n" + bindingJson + "\n" + (existingProjectManifestJson ?? string.Empty) +
                "\n" + (existingBindingJson ?? string.Empty) +
                "\n" + projectDestination + "\n" + bindingDestination)))
            : null;
        if (canApply)
        {
            steps.Add("项目声明通过内置 v1 Schema 和语义校验");
            steps.Add("所有组件已唯一匹配当前主机原生资源");
            steps.Add("端口、目录和现有 CompanyOps 声明未发现冲突");
            steps.Add("确认后只导入 ProjectManifest 和 EnvironmentBinding，不控制业务服务");
        }

        var result = new ExistingProjectOnboardingResult(
            ExistingProjectOnboardingAction.Plan,
            canApply ? OperationOutcome.Succeeded : OperationOutcome.Rejected,
            projectId,
            displayName,
            environment,
            hostId,
            canApply,
            false,
            planToken,
            components,
            ports,
            [],
            steps,
            problems,
            canApply ? null : "onboarding_preflight_failed",
            canApply ? "只读接入预检通过。" : "只读接入预检未通过，请按问题提示修正。" );
        return new OnboardingPlan(
            result,
            projectId ?? string.Empty,
            environment,
            projectRoot ?? string.Empty,
            manifest ?? new JsonObject(),
            binding ?? new JsonObject(),
            manifestJson,
            bindingJson,
            existingProjectManifestJson,
            existingBindingJson,
            projectDestination,
            bindingDestination);
    }

    private void ValidateProjectManifest(JsonObject manifest, ICollection<string> problems)
    {
        if (!string.Equals(manifest["manifestKind"]?.GetValue<string>(), "ProjectManifest", StringComparison.Ordinal))
        {
            problems.Add("ops\\project-manifest.json 不是 ProjectManifest");
            return;
        }

        ValidateAgainstSchema(manifest, "project-manifest.schema.json", "ProjectManifest", problems);
        foreach (var error in ManifestSemanticValidator.Validate("ProjectManifest", manifest))
        {
            problems.Add(error);
        }
        foreach (var component in manifest["components"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if ((component["health"]?.AsArray().Count ?? 0) == 0)
            {
                problems.Add($"组件 {component["id"]?.GetValue<string>()} 没有健康探针，不符合 L1 接入要求");
            }
        }

        foreach (var value in GetJsonStrings(manifest))
        {
            if (value.Contains("change-me", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("请替换", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("待填写", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"ProjectManifest 仍包含占位值：{value}");
            }
            if (Path.IsPathFullyQualified(value))
            {
                problems.Add($"ProjectManifest 不得包含主机绝对路径：{value}");
            }
        }
    }

    private void ValidateBinding(JsonObject binding, ICollection<string> problems)
    {
        ValidateAgainstSchema(binding, "environment-binding.schema.json", "EnvironmentBinding", problems);
        foreach (var error in ManifestSemanticValidator.Validate("EnvironmentBinding", binding))
        {
            problems.Add(error);
        }
    }

    private void ValidateAgainstSchema(
        JsonObject document,
        string schemaFile,
        string label,
        ICollection<string> problems)
    {
        var schemaPath = Path.Combine(_paths.SchemaDirectory, schemaFile);
        if (!File.Exists(schemaPath))
        {
            problems.Add($"Agent 缺少内置 Schema：{schemaFile}");
            return;
        }

        var evaluation = ManifestCatalog.LoadSchema(schemaPath).Evaluate(
            JsonSerializer.SerializeToElement(document),
            new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        if (!evaluation.IsValid)
        {
            problems.Add($"未通过 {label} JSON Schema 校验");
        }
    }

    private async Task<ExistingManifestDocuments> ValidateHostConflictsAsync(
        string projectId,
        string environment,
        string installRoot,
        JsonObject proposedManifest,
        IReadOnlyList<OnboardingComponentProposal> components,
        JsonObject proposedBinding,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        var catalog = await manifestCatalog.InspectAsync(cancellationToken);
        var documents = new List<(ManifestCatalogEntry Entry, JsonObject Root)>();
        foreach (var entry in catalog.Entries.Where(entry => entry.IsValid))
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(entry.Path, cancellationToken)) as JsonObject;
            if (root is not null)
            {
                documents.Add((entry, root));
            }
        }

        var projectManifests = documents.Where(item =>
            item.Entry.ManifestKind == "ProjectManifest" && item.Entry.ProjectId == projectId).ToArray();
        string? existingProjectManifestJson = null;
        if (projectManifests.Length > 1)
        {
            problems.Add($"CompanyOps 已存在多份项目 ID 为 {projectId} 的 ProjectManifest");
        }
        else if (projectManifests.Length == 1)
        {
            existingProjectManifestJson = await File.ReadAllTextAsync(
                projectManifests[0].Entry.Path,
                cancellationToken);
            if (!ProjectManifestEvolutionIsSafe(projectManifests[0].Root, proposedManifest))
            {
                problems.Add(
                    $"项目 {projectId} 的新 ProjectManifest 删除了现有组件或改变了组件 kind；拒绝以重新接入替代受控迁移");
            }
        }

        var bindings = documents.Where(item => item.Entry.ManifestKind == "EnvironmentBinding").ToArray();
        var sameBinding = bindings.Where(item =>
            GetString(item.Root, "metadata", "projectId") == projectId &&
            GetString(item.Root, "metadata", "environment") == environment &&
            GetString(item.Root, "metadata", "hostId") == _paths.HostId).ToArray();
        string? existingBindingJson = null;
        if (sameBinding.Length > 1)
        {
            problems.Add($"当前主机已存在多份 {projectId}/{environment} EnvironmentBinding");
        }
        else if (sameBinding.Length == 1)
        {
            var existingBinding = sameBinding[0].Root;
            existingBindingJson = existingBinding.ToJsonString(jsonOptions);
            var existingRevision = existingBinding["metadata"]?["revision"]?.GetValue<int>() ?? 1;
            proposedBinding["metadata"]!["revision"] = existingRevision;
            if (!JsonNode.DeepEquals(existingBinding, proposedBinding))
            {
                if (!BindingEvolutionIsSafe(existingBinding, proposedBinding))
                {
                    problems.Add(
                        $"当前主机已有 {projectId}/{environment} EnvironmentBinding；只允许修正端口或新增保持原绑定不变的组件");
                }
                else if (existingRevision == int.MaxValue)
                {
                    problems.Add($"当前主机 {projectId}/{environment} EnvironmentBinding 修订号已达到上限");
                }
                else
                {
                    proposedBinding["metadata"]!["revision"] = existingRevision + 1;
                }
            }
        }

        foreach (var existing in bindings)
        {
            var existingProject = GetString(existing.Root, "metadata", "projectId");
            var existingEnvironment = GetString(existing.Root, "metadata", "environment");
            var existingHost = GetString(existing.Root, "metadata", "hostId");
            if (!string.Equals(existingHost, _paths.HostId, StringComparison.OrdinalIgnoreCase) ||
                (existingProject == projectId && existingEnvironment == environment))
            {
                continue;
            }

            var existingRoot = existing.Root["roots"]?["install"]?.GetValue<string>();
            if (existingRoot is not null && PathsOverlap(existingRoot, installRoot))
            {
                problems.Add($"安装目录与 {existingProject}/{existingEnvironment} 重叠：{existingRoot}");
            }

            var existingNames = existing.Root["componentBindings"]?.AsArray().OfType<JsonObject>()
                .Select(item => item["nativeName"]?.GetValue<string>())
                .Where(value => value is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var component in components.Where(item => item.NativeName is not null))
            {
                if (existingNames.Contains(component.NativeName!))
                {
                    problems.Add($"原生资源 {component.NativeName} 已由 {existingProject}/{existingEnvironment} 声明");
                }
            }

            foreach (var proposedPort in proposedBinding["portBindings"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                foreach (var existingPort in existing.Root["portBindings"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if (PortsConflict(proposedPort, existingPort))
                    {
                        problems.Add(
                            $"端口 {proposedPort["protocol"]}:{proposedPort["address"]}:{proposedPort["port"]} " +
                            $"已由 {existingProject}/{existingEnvironment} 声明");
                    }
                }
            }
        }

        return new ExistingManifestDocuments(existingProjectManifestJson, existingBindingJson);
    }

    private static bool BindingEvolutionIsSafe(JsonObject existing, JsonObject proposed)
    {
        var existingImmutable = existing.DeepClone().AsObject();
        var proposedImmutable = proposed.DeepClone().AsObject();
        existingImmutable.Remove("portBindings");
        proposedImmutable.Remove("portBindings");
        existingImmutable.Remove("interactiveSession");
        proposedImmutable.Remove("interactiveSession");
        var existingComponents = existingImmutable["componentBindings"]?.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(item => item["componentId"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var proposedComponents = proposedImmutable["componentBindings"]?.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(item => item["componentId"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        existingImmutable.Remove("componentBindings");
        proposedImmutable.Remove("componentBindings");
        existingImmutable["metadata"]!.AsObject().Remove("revision");
        proposedImmutable["metadata"]!.AsObject().Remove("revision");
        return JsonNode.DeepEquals(existingImmutable, proposedImmutable) &&
               existingComponents.All(pair =>
                   proposedComponents.TryGetValue(pair.Key, out var proposedComponent) &&
                   JsonNode.DeepEquals(pair.Value, proposedComponent));
    }

    private static bool ProjectManifestEvolutionIsSafe(JsonObject existing, JsonObject proposed)
    {
        var existingComponents = existing["components"]?.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(item => item["id"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var proposedComponents = proposed["components"]?.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(item => item["id"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        return existingComponents.All(pair =>
            proposedComponents.TryGetValue(pair.Key, out var proposedComponent) &&
            string.Equals(
                pair.Value["kind"]?.GetValue<string>(),
                proposedComponent["kind"]?.GetValue<string>(),
                StringComparison.Ordinal));
    }

    private static bool PortsConflict(JsonObject left, JsonObject right)
    {
        var leftProtocol = left["protocol"]?.GetValue<string>();
        var rightProtocol = right["protocol"]?.GetValue<string>();
        var leftPort = left["port"]?.GetValue<int>();
        var rightPort = right["port"]?.GetValue<int>();
        var leftAddress = left["address"]?.GetValue<string>();
        var rightAddress = right["address"]?.GetValue<string>();
        if (leftProtocol != rightProtocol || leftPort != rightPort || leftAddress is null || rightAddress is null)
        {
            return false;
        }

        var leftIpv6 = leftAddress.Contains(':');
        var rightIpv6 = rightAddress.Contains(':');
        if (leftIpv6 != rightIpv6)
        {
            return false;
        }
        var wildcard = leftIpv6 ? "::" : "0.0.0.0";
        return leftAddress == rightAddress || leftAddress == wildcard || rightAddress == wildcard;
    }

    private static IEnumerable<string> GetJsonStrings(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            yield return text;
            yield break;
        }
        if (node is JsonObject jsonObject)
        {
            foreach (var child in jsonObject.SelectMany(pair => GetJsonStrings(pair.Value)))
            {
                yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.SelectMany(GetJsonStrings))
            {
                yield return child;
            }
        }
    }

    private static OnboardingComponentProposal ResolveNativeResource(
        string componentId,
        string componentDisplayName,
        string kind,
        string projectId,
        string projectDisplayName,
        string projectRoot,
        IReadOnlyDictionary<string, string>? requestedNames,
        InventorySnapshot? inventory)
    {
        var sourceName = kind switch
        {
            "windowsService" => "windows-services",
            "iisSite" or "staticSite" => "iis",
            "scheduledTask" => "scheduled-tasks",
            "interactiveApp" => "interactive-apps",
            _ => null
        };
        if (kind == "interactiveApp")
        {
            return new OnboardingComponentProposal(
                componentId,
                componentDisplayName,
                kind,
                componentId,
                false,
                []);
        }
        if (sourceName is null || inventory is null)
        {
            return new OnboardingComponentProposal(
                componentId,
                componentDisplayName,
                kind,
                null,
                true,
                []);
        }

        var source = inventory.Sections.SingleOrDefault(section => section.Source == sourceName);
        var eligibleItems = source?.Status == InventorySourceStatus.Available
            ? source.Items.Where(item =>
                    kind is not ("iisSite" or "staticSite") ||
                    item.Metadata.GetValueOrDefault("resourceType") == "site")
                .ToArray()
            : [];
        var allNames = eligibleItems
                .Select(item => kind is "iisSite" or "staticSite"
                    ? item.Id.StartsWith("site:", StringComparison.OrdinalIgnoreCase) ? item.Id[5..] : item.Id
                    : item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var ownedNames = eligibleItems
            .Where(item => IsInventoryItemUnderProjectRoot(kind, item, projectRoot))
            .Select(item => kind is "iisSite" or "staticSite"
                ? item.Id.StartsWith("site:", StringComparison.OrdinalIgnoreCase) ? item.Id[5..] : item.Id
                : item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedNames?.TryGetValue(componentId, out var requested) == true &&
            !string.IsNullOrWhiteSpace(requested))
        {
            var match = allNames.SingleOrDefault(name =>
                string.Equals(name, requested.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ownedNames.Contains(match))
            {
                match = null;
            }
            return new OnboardingComponentProposal(
                componentId,
                componentDisplayName,
                kind,
                match,
                match is null,
                Suggest(ownedNames.ToArray(), projectId, projectDisplayName, componentId, componentDisplayName));
        }

        var identities = new[] { projectId, projectDisplayName, componentId, componentDisplayName }
            .Select(NormalizeIdentity)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = ownedNames.Where(name => identities.Contains(NormalizeIdentity(name))).ToArray();
        var nativeName = exact.Length == 1 ? exact[0] : null;
        return new OnboardingComponentProposal(
            componentId,
            componentDisplayName,
            kind,
            nativeName,
            nativeName is null,
            Suggest(ownedNames.ToArray(), projectId, projectDisplayName, componentId, componentDisplayName));
    }

    private static bool IsInventoryItemUnderProjectRoot(
        string kind,
        InventoryItem item,
        string projectRoot)
    {
        if (kind == "scheduledTask")
        {
            // v1 inventory does not yet extract task actions. Manual exact-name selection
            // remains declaration-only and never creates InstalledState.
            return true;
        }

        var configuredPath = kind switch
        {
            "windowsService" => ExtractExecutablePath(item.Metadata.GetValueOrDefault("binaryPath")),
            "iisSite" or "staticSite" => item.Metadata.GetValueOrDefault("physicalPath"),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        try
        {
            var resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            var resolvedPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            return resolvedPath.StartsWith(
                resolvedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryBuildInteractiveBinding(
        JsonObject component,
        string componentId,
        string projectRoot,
        out string? nativeName,
        out string? problem)
    {
        nativeName = componentId;
        problem = null;
        var executable = component["interactive"]?["executable"]?.GetValue<string>();
        var workingDirectory = component["interactive"]?["workingDirectory"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(workingDirectory))
        {
            problem = $"组件 {componentId} 缺少 interactive 入口或工作目录";
            return false;
        }
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            var exe = Path.GetFullPath(Path.Combine(root, executable));
            var cwd = Path.GetFullPath(Path.Combine(root, workingDirectory));
            var prefix = root + Path.DirectorySeparatorChar;
            if (!exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (!string.Equals(cwd, root, StringComparison.OrdinalIgnoreCase) &&
                 !cwd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                problem = $"组件 {componentId} 必须声明项目目录内的 .exe 和工作目录";
                return false;
            }
            if (!File.Exists(exe) || !Directory.Exists(cwd))
            {
                problem = $"组件 {componentId} 的声明 EXE 或工作目录在服务器上不存在";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"组件 {componentId} 的交互程序路径无效";
            return false;
        }
    }

    private static string? ExtractExecutablePath(string? binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var value = binaryPath.Trim();
        if (value[0] == '"')
        {
            var closing = value.IndexOf('"', 1);
            return closing > 1 ? value[1..closing] : null;
        }

        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd >= 0 ? value[..(exeEnd + 4)] : value.Split(' ', 2)[0];
    }

    private static IReadOnlyList<string> Suggest(
        IReadOnlyList<string> allNames,
        params string[] identities)
    {
        var tokens = identities.Select(NormalizeIdentity).Where(value => value.Length >= 3).ToArray();
        return allNames
            .Where(name => tokens.Any(token => NormalizeIdentity(name).Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Take(20)
            .ToArray();
    }

    private static string NormalizeIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeEnvironment(string value, ICollection<string> problems)
    {
        var environment = string.IsNullOrWhiteSpace(value) ? "production" : value.Trim().ToLowerInvariant();
        if (!IsSafeSegment(environment) || !char.IsLetter(environment[0]))
        {
            problems.Add("环境名只能使用小写字母、数字和短横线，并且必须以字母开头");
        }
        return environment;
    }

    private static string? ResolveOptionalRoot(
        string? input,
        string? fallback,
        string label,
        ICollection<string> problems) =>
        string.IsNullOrWhiteSpace(input)
            ? fallback
            : ResolveLocalDirectory(input, label, problems);

    private static string? ResolveLocalDirectory(
        string input,
        string label,
        ICollection<string> problems)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            problems.Add($"{label}不能为空");
            return null;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim())));
            var root = Path.GetPathRoot(fullPath);
            if (!Path.IsPathFullyQualified(fullPath) ||
                root is null ||
                !root.EndsWith(@":\", StringComparison.Ordinal) ||
                string.Equals(fullPath, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}必须是本机磁盘内的非根目录绝对路径");
                return null;
            }
            if (!Directory.Exists(fullPath))
            {
                problems.Add($"{label}不存在：{fullPath}");
                return null;
            }
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add($"{label}无效：{exception.Message}");
            return null;
        }
    }

    private static async Task<bool> WriteNewOrConfirmIdenticalAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            var existing = await File.ReadAllTextAsync(destination, cancellationToken);
            if (!JsonEquivalent(existing, content))
            {
                throw new InvalidDataException($"目标清单已存在且内容不同，拒绝覆盖：{destination}");
            }
            return false;
        }

        var temporary = destination + $".{Guid.CreateVersion7():N}.tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        try
        {
            File.Move(temporary, destination, overwrite: false);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<ManifestWriteResult> WriteNewOrReplaceAsync(
        string destination,
        string content,
        string? expectedExistingContent,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destination))
        {
            if (expectedExistingContent is not null)
            {
                throw new InvalidDataException($"预检后的原绑定已不存在，请重新检查：{destination}");
            }

            var created = await WriteNewOrConfirmIdenticalAsync(destination, content, cancellationToken);
            return new ManifestWriteResult(created, false, null);
        }

        var existing = await File.ReadAllTextAsync(destination, cancellationToken);
        if (expectedExistingContent is null || !JsonEquivalent(existing, expectedExistingContent))
        {
            throw new InvalidDataException($"预检后的原绑定已变化，请重新检查：{destination}");
        }
        if (JsonEquivalent(existing, content))
        {
            return new ManifestWriteResult(false, false, null);
        }

        await WriteReplacingFileAsync(destination, content, cancellationToken);
        return new ManifestWriteResult(false, true, existing);
    }

    private static async Task WriteReplacingFileAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.CreateVersion7():N}.tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool JsonEquivalent(string left, string right)
    {
        var leftNode = JsonNode.Parse(left);
        var rightNode = JsonNode.Parse(right);
        return JsonNode.DeepEquals(leftNode, rightNode);
    }

    private static void TryDeleteCreatedFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The failure result still reports the original error. Cleanup is best effort.
        }
    }

    private static void TryRollbackWrite(string path, ManifestWriteResult write)
    {
        try
        {
            if (write.Created)
            {
                TryDeleteCreatedFile(path);
            }
            else if (write.Updated && write.PreviousContent is not null)
            {
                WriteReplacingFileAsync(path, write.PreviousContent, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch
        {
            // The failure result still reports the original error. Rollback is best effort.
        }
    }

    private async Task TryAuditFailureAsync(
        OnboardingPlan plan,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.AppendAuditEventAsync(
                new AuditEvent(
                    Guid.CreateVersion7().ToString(),
                    DateTimeOffset.UtcNow,
                    "onboarding",
                    "apply-existing-project",
                    "failed",
                    $"{plan.ProjectId}/{plan.Environment}: {detail}"),
                cancellationToken);
        }
        catch
        {
            // Do not hide the onboarding result when audit persistence is unavailable.
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonObject root, string objectName, string propertyName) =>
        root[objectName]?[propertyName]?.GetValue<string>();

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private sealed record OnboardingPlan(
        ExistingProjectOnboardingResult Result,
        string ProjectId,
        string Environment,
        string ProjectRoot,
        JsonObject ProjectManifest,
        JsonObject Binding,
        string ProjectManifestJson,
        string BindingJson,
        string? ExistingProjectManifestJson,
        string? ExistingBindingJson,
        string ProjectDestination,
        string BindingDestination);

    private sealed record ManifestWriteResult(bool Created, bool Updated, string? PreviousContent);

    private sealed record ExistingManifestDocuments(
        string? ProjectManifestJson,
        string? BindingJson);
}
