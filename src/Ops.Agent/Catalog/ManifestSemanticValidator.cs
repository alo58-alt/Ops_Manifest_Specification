using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CompanyOps.Agent.Catalog;

public static class ManifestSemanticValidator
{
    public static IReadOnlyList<string> Validate(string manifestKind, JsonNode document)
    {
        var errors = new List<string>();
        switch (manifestKind)
        {
            case "ProjectManifest":
                ValidateProjectManifest(document, errors);
                break;
            case "ReleaseManifest":
                ValidateReleaseManifest(document, errors);
                break;
            case "EnvironmentBinding":
                ValidateEnvironmentBinding(document, errors);
                break;
            case "InstalledState":
                AddDuplicateErrors(
                    document["components"]?.AsArray(),
                    "componentId",
                    "安装组件状态",
                    errors);
                break;
            case "PortRegistry":
                ValidatePortCollection(document["reservations"]?.AsArray(), "主机端口登记", errors);
                break;
        }

        return errors;
    }

    private static void ValidateProjectManifest(JsonNode document, List<string> errors)
    {
        var components = document["components"]?.AsArray() ?? [];
        AddDuplicateErrors(components, "id", "组件 ID", errors);

        var componentIds = components
            .Select(GetId)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var componentNode in components)
        {
            if (componentNode is not JsonObject component)
            {
                continue;
            }

            var componentId = GetString(component, "id");
            if (componentId is null)
            {
                continue;
            }

            var componentDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependencyNode in component["dependsOn"]?.AsArray() ?? [])
            {
                var dependency = dependencyNode?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    continue;
                }

                if (!componentIds.Contains(dependency))
                {
                    errors.Add($"组件 {componentId} 依赖不存在的组件 {dependency}");
                }
                else if (string.Equals(componentId, dependency, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"组件 {componentId} 不得依赖自身");
                }

                componentDependencies.Add(dependency);
            }

            dependencies[componentId] = componentDependencies;

            if (string.Equals(GetString(component, "kind"), "pm2Legacy", StringComparison.Ordinal) &&
                component["pm2"] is JsonObject declaredPm2)
            {
                ValidatePm2Script(componentId, GetString(declaredPm2, "script"), errors);
            }
        }

        if (HasDependencyCycle(componentIds, dependencies))
        {
            errors.Add("组件依赖包含循环，无法形成安全启动顺序");
        }

        var ports = document["ports"]?.AsArray() ?? [];
        AddDuplicateErrors(ports, "id", "端口请求 ID", errors);
        foreach (var portNode in ports)
        {
            if (portNode is not JsonObject port)
            {
                continue;
            }

            var componentId = GetString(port, "componentId");
            if (componentId is not null && !componentIds.Contains(componentId))
            {
                errors.Add($"端口请求 {GetString(port, "id")} 引用了不存在的组件 {componentId}");
            }
        }

        AddDuplicateErrors(document["configuration"]?.AsArray(), "key", "配置键", errors);
        AddDuplicateErrors(document["dataDirectories"]?.AsArray(), "id", "数据目录 ID", errors);
    }

    private static void ValidateReleaseManifest(JsonNode document, List<string> errors)
    {
        var artifacts = document["artifacts"]?.AsArray() ?? [];
        AddDuplicateErrors(artifacts, "id", "制品 ID", errors);
        AddDuplicateErrors(artifacts, "fileName", "制品文件名", errors);
        var payloads = document["componentPayloads"]?.AsArray() ?? [];
        AddDuplicateErrors(payloads, "componentId", "组件载荷 componentId", errors);
        var artifactIds = artifacts
            .Select(GetId)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var payloadNode in payloads)
        {
            if (payloadNode is not JsonObject payload)
            {
                continue;
            }

            var artifactId = GetString(payload, "artifactId");
            if (artifactId is not null && !artifactIds.Contains(artifactId))
            {
                errors.Add(
                    $"组件 {GetString(payload, "componentId")} 的入口 {GetString(payload, "entrypoint")} " +
                    $"引用了不存在的制品 {artifactId}");
            }

            if (payload["pm2"] is JsonObject pm2)
            {
                var componentId = GetString(payload, "componentId") ?? "<unknown>";
                var script = GetString(pm2, "script");
                ValidatePm2Script(componentId, script, errors);
                if (!string.Equals(script, GetString(payload, "path"), StringComparison.Ordinal))
                {
                    errors.Add($"PM2 组件 {componentId} 的 pm2.script 必须与 path 完全一致");
                }

                if (!string.Equals(GetString(pm2, "cwd"), GetString(payload, "workingDirectory"), StringComparison.Ordinal))
                {
                    errors.Add($"PM2 组件 {componentId} 的 pm2.cwd 必须与 workingDirectory 完全一致");
                }

                var typedArguments = pm2["arguments"]?.AsArray() ?? [];
                var genericArguments = payload["arguments"]?.AsArray() ?? [];
                if (!typedArguments.Select(static value => value?.GetValue<string>()).SequenceEqual(
                        genericArguments.Select(static value => value?.GetValue<string>()),
                        StringComparer.Ordinal))
                {
                    errors.Add($"PM2 组件 {componentId} 的 pm2.arguments 必须与 arguments 完全逐项一致");
                }

                ValidatePm2Arguments(componentId, typedArguments, errors);
            }
        }
    }

    private static void ValidatePm2Script(string componentId, string? script, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
        var fileName = Path.GetFileName(script.Replace('/', Path.DirectorySeparatorChar));
        if (Regex.IsMatch(
                fileName,
                @"^(?:(?:cmd|powershell|pwsh|bash|sh|wscript|cscript)(?:\.exe)?|.+\.(?:cmd|bat|ps1|psm1|vbs|wsf))$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            errors.Add($"PM2 组件 {componentId} 不得以命令宿主或脚本文件作为入口");
        }
    }

    private static void ValidatePm2Arguments(string componentId, JsonArray arguments, List<string> errors)
    {
        foreach (var node in arguments)
        {
            var argument = node?.GetValue<string>() ?? string.Empty;
            if (Regex.IsMatch(argument, @"^(?:&&|\|\||[&|;<>]|>>|2>|2>>)$", RegexOptions.CultureInvariant))
            {
                errors.Add($"PM2 组件 {componentId} 的参数不得包含独立 shell 控制符");
            }

            if (Regex.IsMatch(
                    argument,
                    @"^(?:--?|/)?(?:password|passwd|pwd|token|secret|api[-_]?key)(?:=|:).+",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                errors.Add($"PM2 组件 {componentId} 的参数疑似包含 Secret 明文；必须使用主机 Secret 引用或配置绑定");
            }
        }
    }

    private static void ValidateEnvironmentBinding(JsonNode document, List<string> errors)
    {
        AddDuplicateErrors(
            document["componentBindings"]?.AsArray(),
            "componentId",
            "组件绑定",
            errors);
        AddDuplicateErrors(document["settings"]?.AsArray(), "key", "环境配置键", errors);
        ValidatePortCollection(document["portBindings"]?.AsArray(), "环境端口绑定", errors);

        foreach (var settingNode in document["settings"]?.AsArray() ?? [])
        {
            if (settingNode is not JsonObject setting)
            {
                continue;
            }

            var hasValue = setting.ContainsKey("value");
            var hasSecretReference = setting.ContainsKey("secretRef");
            if (hasValue == hasSecretReference)
            {
                errors.Add(
                    $"环境配置 {GetString(setting, "key")} 必须且只能包含 value 或 secretRef 之一");
            }
        }
    }

    private static void ValidatePortCollection(
        JsonArray? ports,
        string label,
        List<string> errors)
    {
        if (ports is null)
        {
            return;
        }

        for (var leftIndex = 0; leftIndex < ports.Count; leftIndex++)
        {
            if (ports[leftIndex] is not JsonObject left)
            {
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < ports.Count; rightIndex++)
            {
                if (ports[rightIndex] is not JsonObject right)
                {
                    continue;
                }

                var leftProtocol = GetString(left, "protocol");
                var rightProtocol = GetString(right, "protocol");
                var leftPort = GetInt(left, "port");
                var rightPort = GetInt(right, "port");
                if (!string.Equals(leftProtocol, rightProtocol, StringComparison.OrdinalIgnoreCase) ||
                    leftPort is null ||
                    leftPort != rightPort)
                {
                    continue;
                }

                var leftAddress = GetString(left, "address");
                var rightAddress = GetString(right, "address");
                if (leftAddress is null || rightAddress is null)
                {
                    continue;
                }

                var leftIsIpv6 = leftAddress.Contains(':', StringComparison.Ordinal);
                var rightIsIpv6 = rightAddress.Contains(':', StringComparison.Ordinal);
                if (leftIsIpv6 != rightIsIpv6)
                {
                    continue;
                }

                var wildcard = leftIsIpv6 ? "::" : "0.0.0.0";
                if (string.Equals(leftAddress, rightAddress, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leftAddress, wildcard, StringComparison.Ordinal) ||
                    string.Equals(rightAddress, wildcard, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{label}冲突：{leftProtocol} {leftAddress}:{leftPort} 与 {rightAddress}:{rightPort}");
                }
            }
        }
    }

    private static bool HasDependencyCycle(
        HashSet<string> componentIds,
        Dictionary<string, HashSet<string>> dependencies)
    {
        var visitState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string componentId)
        {
            if (visitState.TryGetValue(componentId, out var state))
            {
                return state == 1;
            }

            visitState[componentId] = 1;
            if (dependencies.TryGetValue(componentId, out var componentDependencies))
            {
                foreach (var dependency in componentDependencies.Where(componentIds.Contains))
                {
                    if (Visit(dependency))
                    {
                        return true;
                    }
                }
            }

            visitState[componentId] = 2;
            return false;
        }

        return componentIds.Any(Visit);
    }

    private static void AddDuplicateErrors(
        JsonArray? items,
        string propertyName,
        string label,
        List<string> errors)
    {
        if (items is null)
        {
            return;
        }

        foreach (var duplicate in items
                     .OfType<JsonObject>()
                     .Select(item => GetString(item, propertyName))
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(static value => value!, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            errors.Add($"{label}重复：{duplicate.Key}");
        }
    }

    private static string? GetId(JsonNode? node) =>
        node is JsonObject jsonObject ? GetString(jsonObject, "id") : null;

    private static string? GetString(JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName]?.GetValue<string>();

    private static int? GetInt(JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName]?.GetValue<int>();
}
