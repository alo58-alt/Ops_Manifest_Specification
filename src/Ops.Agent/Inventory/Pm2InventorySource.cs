using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class Pm2InventorySource(
    ILegacyPm2ClaimProvider claimProvider,
    Pm2SnapshotReader snapshotReader) : IInventorySource
{
    public string Name => "pm2-legacy";

    public async Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        var claims = await claimProvider.GetClaimsAsync(cancellationToken);
        if (claims.Count == 0)
        {
            return new InventorySection(
                Name,
                InventorySourceStatus.Available,
                [],
                "当前主机没有声明 pm2Legacy 组件");
        }

        var items = new List<InventoryItem>();
        var hasReadableSnapshot = false;
        foreach (var claim in claims)
        {
            var snapshotResult = await snapshotReader.ReadAsync(claim, cancellationToken);
            var ownership = Pm2OwnershipEvaluator.Evaluate(claim, snapshotResult);
            hasReadableSnapshot |= snapshotResult.Snapshot is not null;
            var process = ownership.Process;
            items.Add(
                new InventoryItem(
                    $"{claim.ProjectId}/{claim.Environment}/{claim.ComponentId}",
                    claim.DisplayName,
                    ownership.State.ToString(),
                    new Dictionary<string, string?>
                    {
                        ["projectId"] = claim.ProjectId,
                        ["environment"] = claim.Environment,
                        ["componentId"] = claim.ComponentId,
                        ["name"] = claim.ProcessName,
                        ["expectedCwd"] = claim.ExpectedCwd,
                        ["expectedScript"] = claim.ExpectedScript,
                        ["pmId"] = process?.PmId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["pid"] = process?.Pid.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["runtimeStatus"] = process?.Status,
                        ["detail"] = ownership.Detail
                    }));
        }

        return new InventorySection(
            Name,
            hasReadableSnapshot
                ? InventorySourceStatus.Available
                : InventorySourceStatus.Unavailable,
            items,
            "只消费 owner 上下文生成的缩减快照；Agent 不直接执行 pm2 jlist");
    }
}
