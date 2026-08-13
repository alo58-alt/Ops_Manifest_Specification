using System.Security.Principal;
using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.SessionAgent;

public sealed class InteractiveStartupWorker(
    InteractiveClaimReader claims,
    InteractiveProcessManager processes,
    ILogger<InteractiveStartupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await processes.EnsureLogonAppsStartedAsync(await claims.ReadAsync(stoppingToken), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "交互程序登录启动扫描失败"); }
    }
}

public sealed class InteractiveSnapshotWorker(
    InteractiveClaimReader claims,
    InteractiveProcessManager processes,
    IOptions<SessionAgentOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<InteractiveSnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SnapshotIntervalSeconds));
        do
        {
            try { await CaptureAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "交互程序快照失败"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前用户 SID");
        var allClaims = await claims.ReadAsync(cancellationToken);
        Directory.CreateDirectory(options.Value.SnapshotDirectory);
        var snapshotRoot = Path.GetFullPath(options.Value.SnapshotDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var group in allClaims.GroupBy(static claim => claim.SnapshotFileName, StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.GetFullPath(Path.Combine(snapshotRoot, group.Key));
            if (!destination.StartsWith(snapshotRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var groupClaims = group.ToArray();
            var snapshot = new InteractiveAppSnapshot(
                InteractiveSessionProtocol.SnapshotVersion,
                ownerSid,
                Environment.ProcessId == 0 ? 0 : System.Diagnostics.Process.GetCurrentProcess().SessionId,
                DateTimeOffset.UtcNow,
                processes.Snapshot(groupClaims));
            var temporary = destination + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, jsonOptions), cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
    }
}
