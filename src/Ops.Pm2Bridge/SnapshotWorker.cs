using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace CompanyOps.Pm2Bridge;

public sealed class SnapshotWorker(
    Pm2CliRunner runner,
    IOptions<BridgeOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<SnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.SnapshotIntervalSeconds, 5, 300));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await CaptureAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "PM2 缩减快照生成失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前 owner SID");
        var snapshotFiles = await FindSnapshotFilesAsync(ownerSid, cancellationToken);
        if (snapshotFiles.Count == 0)
        {
            return;
        }

        var result = await runner.ListAsync(cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("PM2 jlist 失败：{Detail}", result.Detail);
            return;
        }

        var snapshot = new
        {
            protocolVersion = "ops-pm2-snapshot/v1",
            ownerSid,
            capturedAt = DateTimeOffset.UtcNow,
            daemonPid = 0,
            processes = result.Processes.Select(process => new
            {
                process.Name,
                process.PmId,
                process.Cwd,
                process.Script,
                process.Status,
                process.Pid,
                process.RestartCount
            })
        };
        Directory.CreateDirectory(options.Value.SnapshotDirectory);
        foreach (var fileName in snapshotFiles)
        {
            var destination = Path.GetFullPath(Path.Combine(options.Value.SnapshotDirectory, fileName));
            var root = Path.GetFullPath(options.Value.SnapshotDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var temporary = destination + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, jsonOptions), cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
    }

    private async Task<IReadOnlyList<string>> FindSnapshotFilesAsync(
        string ownerSid,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.Value.ManifestDirectory))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(options.Value.ManifestDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024)
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject;
                var legacy = root?["manifestKind"]?.GetValue<string>() == "EnvironmentBinding"
                    ? root["legacyPm2"] as JsonObject
                    : null;
                var fileName = legacy?["snapshotFileName"]?.GetValue<string>();
                if (legacy?["ownerSid"]?.GetValue<string>() == ownerSid &&
                    fileName is not null && Path.GetFileName(fileName) == fileName)
                {
                    result.Add(fileName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning("跳过无法解析的 binding {Path}：{Message}", path, exception.Message);
            }
        }

        return result.ToArray();
    }
}
