using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class InteractiveAppInventorySource(
    IInteractiveSessionClaimProvider claims,
    InteractiveSnapshotReader snapshots) : IInventorySource
{
    public string Name => "interactive-apps";

    public async Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        var allClaims = await claims.GetClaimsAsync(cancellationToken);
        if (allClaims.Count == 0) return new(Name, InventorySourceStatus.Available, [], "当前主机没有声明 interactiveApp 组件");
        var items = new List<InventoryItem>();
        var anySnapshot = false;
        foreach (var claim in allClaims)
        {
            var read = await snapshots.ReadAsync(claim, cancellationToken);
            anySnapshot |= read.Snapshot is not null;
            var matches = read.Snapshot?.Processes.Where(process =>
                process.ProjectId == claim.ProjectId && process.Environment == claim.Environment &&
                process.ComponentId == claim.ComponentId).ToArray() ?? [];
            var process = matches.Length == 1 ? matches[0] : null;
            var exact = process is not null &&
                string.Equals(process.Executable, claim.ExpectedExecutable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(process.WorkingDirectory, claim.ExpectedWorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
                process.Arguments.SequenceEqual(claim.ExpectedArguments, StringComparer.Ordinal);
            var state = read.State != "Available" ? read.State : matches.Length != 1 ? "Conflict" : exact ? "Matched" : "Conflict";
            items.Add(new InventoryItem(
                $"{claim.ProjectId}/{claim.Environment}/{claim.ComponentId}", claim.DisplayName, state,
                new Dictionary<string, string?>
                {
                    ["projectId"] = claim.ProjectId, ["environment"] = claim.Environment,
                    ["componentId"] = claim.ComponentId, ["ownerSid"] = claim.OwnerSid,
                    ["sessionId"] = read.Snapshot?.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["executable"] = process?.Executable, ["workingDirectory"] = process?.WorkingDirectory,
                    ["runtimeStatus"] = process?.State, ["pid"] = process?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["detail"] = state == "Matched" ? "用户会话、EXE、工作目录和参数唯一精确匹配" : read.Detail
                }));
        }
        return new(Name, anySnapshot ? InventorySourceStatus.Available : InventorySourceStatus.Unavailable, items,
            "只消费登录用户 Session Agent 快照；Windows Service 不直接创建桌面窗口");
    }
}
