using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyOps.Agent.Inventory;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Onboarding;

public sealed record Pm2OwnerDiscoveryResult(
    string? OwnerSid,
    string? SnapshotFileName,
    string? ControlPipeName,
    IReadOnlyDictionary<string, Pm2OwnershipResult> Components,
    IReadOnlyList<string> Problems)
{
    public bool Success =>
        OwnerSid is not null &&
        SnapshotFileName is not null &&
        ControlPipeName is not null &&
        Problems.Count == 0;
}

public sealed class Pm2OwnerDiscoveryService(
    OpsPathResolver pathResolver,
    JsonSerializerOptions jsonOptions)
{
    private const int MaximumDiscoveryFiles = 64;
    private const int DiscoveryMaxAgeSeconds = 30;
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();
    private readonly Pm2SnapshotReader _snapshotReader = new(pathResolver, jsonOptions);

    public async Task<Pm2OwnerDiscoveryResult> DiscoverAsync(
        JsonObject manifest,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var components = manifest["components"]?.AsArray().OfType<JsonObject>()
            .Where(static component =>
                string.Equals(
                    component["kind"]?.GetValue<string>(),
                    "pm2Legacy",
                    StringComparison.Ordinal))
            .ToArray() ?? [];
        if (components.Length == 0)
        {
            return new Pm2OwnerDiscoveryResult(
                null,
                null,
                null,
                new Dictionary<string, Pm2OwnershipResult>(StringComparer.Ordinal),
                []);
        }

        if (!Directory.Exists(_paths.Pm2SnapshotDirectory))
        {
            return Failed("当前主机尚未发现 PM2 Owner Bridge；请先完成一次主机级 PM2 接管配置");
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(
                    _paths.Pm2SnapshotDirectory,
                    Pm2SnapshotProtocol.DiscoverySearchPattern,
                    SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Take(MaximumDiscoveryFiles + 1)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failed($"PM2 owner 发现快照目录不可读：{exception.Message}");
        }
        if (files.Length == 0)
        {
            return Failed("当前主机没有 PM2 Owner Bridge 发现快照；Bridge 必须由真实 PM2 owner 运行");
        }
        if (files.Length > MaximumDiscoveryFiles)
        {
            return Failed("PM2 owner 发现快照数量超过安全上限，拒绝猜测 daemon 归属");
        }

        var readable = new List<(string FileName, Pm2Snapshot Snapshot)>();
        var readProblems = new List<string>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            var read = await _snapshotReader.ReadDiscoveryAsync(
                fileName,
                DiscoveryMaxAgeSeconds,
                cancellationToken);
            if (read.State == Pm2OwnershipState.Matched && read.Snapshot is not null)
            {
                readable.Add((fileName, read.Snapshot));
            }
            else
            {
                readProblems.Add($"{fileName}：{read.Detail}");
            }
        }

        if (readable.Count == 0)
        {
            return Failed(
                "没有可用的新鲜 PM2 owner 发现快照",
                readProblems.Take(5));
        }

        var candidates = new List<OwnerCandidate>();
        var mismatchProblems = new List<string>();
        foreach (var item in readable)
        {
            var matches = new Dictionary<string, Pm2OwnershipResult>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                var componentId = component["id"]?.GetValue<string>() ?? string.Empty;
                var processName = component["pm2"]?["name"]?.GetValue<string>() ?? string.Empty;
                var cwd = component["pm2"]?["cwd"]?.GetValue<string>();
                var script = component["pm2"]?["script"]?.GetValue<string>();
                if (!TryResolveProjectPath(projectRoot, cwd, allowRoot: true, out var expectedCwd) ||
                    !TryResolveProjectPath(projectRoot, script, allowRoot: false, out var expectedScript))
                {
                    matches[componentId] = new Pm2OwnershipResult(
                        Pm2OwnershipState.Unbound,
                        "PM2 cwd/script 无法安全解析到项目目录内");
                    continue;
                }

                var claim = new LegacyPm2Claim(
                    manifest["metadata"]?["id"]?.GetValue<string>() ?? string.Empty,
                    string.Empty,
                    _paths.HostId,
                    componentId,
                    component["displayName"]?.GetValue<string>() ?? componentId,
                    processName,
                    expectedCwd,
                    expectedScript,
                    item.Snapshot.OwnerSid,
                    item.FileName,
                    item.Snapshot.ControlPipeName,
                    DiscoveryMaxAgeSeconds,
                    null);
                matches[componentId] = Pm2OwnershipEvaluator.Evaluate(
                    claim,
                    new Pm2SnapshotReadResult(
                        item.Snapshot,
                        Pm2OwnershipState.Matched,
                        "PM2 owner 发现快照可用"));
            }

            if (matches.Count == components.Length &&
                matches.Values.All(static match => match.State == Pm2OwnershipState.Matched))
            {
                var matchedPmIds = matches.Values
                    .Select(static match => match.Process!.PmId)
                    .ToArray();
                if (matchedPmIds.Distinct().Count() != matchedPmIds.Length)
                {
                    mismatchProblems.Add(
                        $"PM2 owner {item.Snapshot.OwnerSid} 的多个组件命中了同一个 pm_id");
                }
                else
                {
                    candidates.Add(new OwnerCandidate(item.FileName, item.Snapshot, matches));
                }
            }
            else
            {
                mismatchProblems.AddRange(matches
                    .Where(static pair => pair.Value.State != Pm2OwnershipState.Matched)
                    .Select(pair =>
                        $"PM2 owner {item.Snapshot.OwnerSid} 的组件 {pair.Key}：{pair.Value.Detail}"));
            }
        }

        if (candidates.Count == 0)
        {
            return Failed(
                "没有一个 PM2 owner 同时唯一匹配项目声明的全部 PM2 组件",
                mismatchProblems.Concat(readProblems).Take(10));
        }
        if (candidates.Count > 1)
        {
            return Failed("多个 PM2 owner 都精确匹配该项目，拒绝猜测 daemon 归属");
        }

        var candidate = candidates[0];
        return new Pm2OwnerDiscoveryResult(
            candidate.Snapshot.OwnerSid,
            candidate.FileName,
            candidate.Snapshot.ControlPipeName,
            candidate.Matches,
            []);

        Pm2OwnerDiscoveryResult Failed(string problem, IEnumerable<string>? details = null) =>
            new(
                null,
                null,
                null,
                new Dictionary<string, Pm2OwnershipResult>(StringComparer.Ordinal),
                new[] { problem }.Concat(details ?? []).ToArray());
    }

    private static bool TryResolveProjectPath(
        string root,
        string? relativePath,
        bool allowRoot,
        out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(fullRoot, relativePath)));
            var inside = candidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            if (!inside && !(allowRoot && string.Equals(
                    candidate,
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            resolvedPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record OwnerCandidate(
        string FileName,
        Pm2Snapshot Snapshot,
        IReadOnlyDictionary<string, Pm2OwnershipResult> Matches);
}
