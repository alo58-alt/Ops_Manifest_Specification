using System.Text.Json;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class InteractiveSnapshotReader(
    OpsPathResolver paths,
    JsonSerializerOptions jsonOptions)
{
    private readonly string _root = paths.Resolve().InteractiveSnapshotDirectory;

    public async Task<InteractiveSnapshotReadResult> ReadAsync(
        InteractiveSessionClaim claim,
        CancellationToken cancellationToken)
    {
        if (claim.BindingError is not null || claim.OwnerSid is null || claim.SnapshotFileName is null)
            return new(null, "Unbound", claim.BindingError ?? "交互会话未绑定");
        if (Path.GetFileName(claim.SnapshotFileName) != claim.SnapshotFileName)
            return new(null, "Conflict", "交互快照文件名不安全");
        var rootPrefix = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPrefix, claim.SnapshotFileName));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return new(null, "Conflict", "交互快照路径逃逸");
        if (!File.Exists(path)) return new(null, "Unavailable", "当前用户 Session Agent 快照不存在");
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) return new(null, "Conflict", "交互快照超过大小限制");
            var snapshot = JsonSerializer.Deserialize<InteractiveAppSnapshot>(await File.ReadAllTextAsync(path, cancellationToken), jsonOptions);
            if (snapshot is null || snapshot.ProtocolVersion != InteractiveSessionProtocol.SnapshotVersion)
                return new(null, "Conflict", "交互快照协议无效");
            if (!string.Equals(snapshot.OwnerSid, claim.OwnerSid, StringComparison.OrdinalIgnoreCase))
                return new(snapshot, "Conflict", "交互快照 owner SID 与绑定不一致");
            var age = DateTimeOffset.UtcNow - snapshot.CapturedAt;
            if (snapshot.SessionId <= 0 || age < TimeSpan.FromMinutes(-5) || age > TimeSpan.FromSeconds(claim.MaxAgeSeconds))
                return new(snapshot, "Stale", "当前用户会话不存在或快照已过期");
            return new(snapshot, "Available", "交互快照可用");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, "Conflict", $"交互快照读取失败：{exception.Message}");
        }
    }
}
