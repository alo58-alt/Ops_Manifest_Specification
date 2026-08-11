using System.Diagnostics;
using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Pm2Bridge;

public sealed record BridgeProcess(
    string Name,
    int PmId,
    string Cwd,
    string Script,
    string Status,
    int Pid,
    int RestartCount);

public sealed record Pm2CommandResult(
    bool Success,
    IReadOnlyList<BridgeProcess> Processes,
    string? Detail = null);

public sealed class Pm2CliRunner(IOptions<BridgeOptions> options)
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly BridgeOptions _options = options.Value;

    public async Task<Pm2CommandResult> ListAsync(CancellationToken cancellationToken)
    {
        var command = await RunAsync([_options.Pm2CliPath, "jlist"], cancellationToken);
        if (!command.Success)
        {
            return new Pm2CommandResult(false, [], command.Detail);
        }

        try
        {
            using var document = JsonDocument.Parse(command.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 10_000)
            {
                return new Pm2CommandResult(false, [], "pm2 jlist 根节点无效或进程数超限");
            }

            var processes = new List<BridgeProcess>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var environment = item.GetProperty("pm2_env");
                processes.Add(new BridgeProcess(
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("pm_id").GetInt32(),
                    environment.TryGetProperty("pm_cwd", out var cwd) ? cwd.GetString() ?? string.Empty : string.Empty,
                    environment.TryGetProperty("pm_exec_path", out var script) ? script.GetString() ?? string.Empty : string.Empty,
                    environment.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                    item.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
                    environment.TryGetProperty("restart_time", out var restart) ? restart.GetInt32() : 0));
            }

            return new Pm2CommandResult(true, processes);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new Pm2CommandResult(false, [], $"pm2 jlist JSON 无效：{exception.Message}");
        }
    }

    public async Task<Pm2CommandResult> ControlAsync(
        Pm2BridgeControlRequest request,
        CancellationToken cancellationToken)
    {
        var current = await ListAsync(cancellationToken);
        if (!current.Success)
        {
            return current;
        }

        var sameName = current.Processes.Where(process =>
            string.Equals(process.Name, request.Name, StringComparison.Ordinal)).ToArray();
        if (sameName.Length != 1)
        {
            return new Pm2CommandResult(false, current.Processes, $"PM2 精确名称匹配数量为 {sameName.Length}");
        }

        var process = sameName[0];
        if (process.PmId != request.PmId ||
            !SamePath(process.Cwd, request.ExpectedCwd) ||
            !SamePath(process.Script, request.ExpectedScript))
        {
            return new Pm2CommandResult(false, current.Processes, "pm_id、cwd 或 script 归属冲突");
        }

        var action = request.Action switch
        {
            ComponentOperationAction.Start => "start",
            ComponentOperationAction.Stop => "stop",
            ComponentOperationAction.Restart => "restart",
            _ => throw new InvalidOperationException("未知 PM2 动作")
        };
        var command = await RunAsync(
            [_options.Pm2CliPath, action, request.PmId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            cancellationToken);
        return command.Success
            ? new Pm2CommandResult(true, current.Processes, $"PM2 {action} pm_id={request.PmId} 完成")
            : new Pm2CommandResult(false, current.Processes, command.Detail);
    }

    private async Task<RawCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.NodeExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return new RawCommandResult(false, string.Empty, "无法启动 Node PM2 CLI");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (stdout.Length > MaximumOutputCharacters || stderr.Length > MaximumOutputCharacters)
            {
                return new RawCommandResult(false, string.Empty, "PM2 CLI 输出超过限制");
            }

            return new RawCommandResult(
                process.ExitCode == 0,
                stdout,
                process.ExitCode == 0 ? null : stderr.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new RawCommandResult(false, string.Empty, "PM2 CLI 执行超时");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new RawCommandResult(false, string.Empty, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private sealed record RawCommandResult(bool Success, string Stdout, string? Detail);
}
