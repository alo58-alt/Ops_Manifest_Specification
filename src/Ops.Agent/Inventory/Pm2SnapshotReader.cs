using System.Text.Json;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed record Pm2SnapshotReadResult(
    Pm2Snapshot? Snapshot,
    Pm2OwnershipState State,
    string Detail);

public sealed class Pm2SnapshotReader(
    OpsPathResolver pathResolver,
    JsonSerializerOptions jsonOptions)
{
    private const long MaximumSnapshotBytes = 4 * 1024 * 1024;
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();

    public async Task<Pm2SnapshotReadResult> ReadAsync(
        LegacyPm2Claim claim,
        CancellationToken cancellationToken)
    {
        if (claim.BindingError is not null ||
            claim.OwnerSid is null ||
            claim.SnapshotFileName is null)
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.Unbound,
                claim.BindingError ?? "PM2 owner/snapshot 未绑定");
        }

        if (!string.Equals(
                Path.GetFileName(claim.SnapshotFileName),
                claim.SnapshotFileName,
                StringComparison.Ordinal))
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.Conflict,
                "PM2 快照文件名包含路径字符");
        }

        var snapshotPath = Path.GetFullPath(
            Path.Combine(_paths.Pm2SnapshotDirectory, claim.SnapshotFileName));
        var snapshotRoot = Path.GetFullPath(_paths.Pm2SnapshotDirectory)
                           + Path.DirectorySeparatorChar;
        if (!snapshotPath.StartsWith(snapshotRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.Conflict,
                "PM2 快照路径逃逸");
        }

        if (!File.Exists(snapshotPath))
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.SnapshotUnavailable,
                $"PM2 缩减快照不存在：{claim.SnapshotFileName}");
        }

        var fileInfo = new FileInfo(snapshotPath);
        if (fileInfo.Length > MaximumSnapshotBytes)
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.Conflict,
                "PM2 快照超过大小限制");
        }

        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
            var snapshot = JsonSerializer.Deserialize<Pm2Snapshot>(json, jsonOptions);
            if (snapshot is null ||
                !string.Equals(
                    snapshot.ProtocolVersion,
                    "ops-pm2-snapshot/v1",
                    StringComparison.Ordinal))
            {
                return new Pm2SnapshotReadResult(
                    null,
                    Pm2OwnershipState.Conflict,
                    "PM2 快照协议版本无效");
            }

            if (!string.Equals(
                    snapshot.OwnerSid,
                    claim.OwnerSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new Pm2SnapshotReadResult(
                    null,
                    Pm2OwnershipState.Conflict,
                    "PM2 快照 owner SID 与 EnvironmentBinding 不一致");
            }

            var age = DateTimeOffset.UtcNow - snapshot.CapturedAt;
            if (age < TimeSpan.FromMinutes(-5) ||
                age > TimeSpan.FromSeconds(claim.MaxAgeSeconds))
            {
                return new Pm2SnapshotReadResult(
                    snapshot,
                    Pm2OwnershipState.SnapshotStale,
                    $"PM2 快照已过期，采集时间 {snapshot.CapturedAt:O}");
            }

            return new Pm2SnapshotReadResult(
                snapshot,
                Pm2OwnershipState.Matched,
                "PM2 快照可用于归属判定");
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return new Pm2SnapshotReadResult(
                null,
                Pm2OwnershipState.Conflict,
                $"PM2 快照读取失败：{exception.Message}");
        }
    }
}
