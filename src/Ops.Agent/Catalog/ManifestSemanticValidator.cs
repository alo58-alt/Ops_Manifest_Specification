using System.Text.Json.Nodes;

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
        var artifactIds = artifacts
            .Select(GetId)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var payloadNode in document["componentPayloads"]?.AsArray() ?? [])
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
