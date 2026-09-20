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
    int RestartCount,
    IReadOnlyList<string> Arguments);

public sealed record Pm2CommandResult(
    bool Success,
    IReadOnlyList<BridgeProcess> Processes,
    string? Detail = null,
    BridgeProcess? Process = null);

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
                    environment.TryGetProperty("restart_time", out var restart) ? restart.GetInt32() : 0,
                    ReadArguments(environment)));
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
            !SamePath(process.Script, request.ExpectedScript) ||
            request.ExpectedArguments is not null &&
            !process.Arguments.SequenceEqual(request.ExpectedArguments, StringComparer.Ordinal))
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

    public async Task<Pm2CommandResult> MutateAsync(
        Pm2BridgeMutationRequest request,
        CancellationToken cancellationToken) => request.Operation switch
        {
            Pm2BridgeMutationAction.Delete when request.ExpectedProcess is not null =>
                await DeleteAsync(request.ExpectedProcess, cancellationToken),
            Pm2BridgeMutationAction.Register when request.Registration is not null =>
                await RegisterAsync(request.Registration, cancellationToken),
            _ => new Pm2CommandResult(false, [], "PM2 变更请求缺少对应的结构化参数")
        };

    private async Task<Pm2CommandResult> DeleteAsync(
        Pm2BridgeProcessIdentity expected,
        CancellationToken cancellationToken)
    {
        var current = await ListAsync(cancellationToken);
        if (!current.Success) return current;
        if (MatchExact(current.Processes, expected) is null)
            return new(false, current.Processes, "PM2 删除前精确身份不唯一或已变化");

        var command = await RunAsync(
            [_options.Pm2CliPath, "delete", expected.PmId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            cancellationToken);
        if (!command.Success) return new(false, current.Processes, command.Detail);

        var after = await ListAsync(cancellationToken);
        if (!after.Success) return after;
        if (after.Processes.Any(process => process.PmId == expected.PmId ||
                                           string.Equals(process.Name, expected.Name, StringComparison.Ordinal)))
            return new(false, after.Processes, "PM2 删除后目标仍存在或同名实例并发出现");
        return new(true, after.Processes, $"PM2 delete pm_id={expected.PmId} 完成");
    }

    private async Task<Pm2CommandResult> RegisterAsync(
        Pm2BridgeRegistration registration,
        CancellationToken cancellationToken)
    {
        if (!SafeRegistration(registration)) return new(false, [], "PM2 登记参数无效");
        var current = await ListAsync(cancellationToken);
        if (!current.Success) return current;
        if (current.Processes.Any(process => string.Equals(process.Name, registration.Name, StringComparison.Ordinal)))
            return new(false, current.Processes, "PM2 登记前已存在同名实例");

        var arguments = new List<string>
        {
            _options.Pm2CliPath, "start", registration.Script,
            "--name", registration.Name, "--cwd", registration.Cwd
        };
        if (registration.Arguments.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(registration.Arguments);
        }
        var command = await RunAsync(arguments, cancellationToken);
        if (!command.Success) return new(false, current.Processes, command.Detail);

        var after = await ListAsync(cancellationToken);
        if (!after.Success) return after;
        var matches = after.Processes.Where(process =>
            string.Equals(process.Name, registration.Name, StringComparison.Ordinal) &&
            SamePath(process.Cwd, registration.Cwd) &&
            SamePath(process.Script, registration.Script) &&
            process.Arguments.SequenceEqual(registration.Arguments, StringComparer.Ordinal) &&
            string.Equals(process.Status, "online", StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? new(true, after.Processes, $"PM2 register pm_id={matches[0].PmId} 完成", matches[0])
            : new(false, after.Processes, $"PM2 登记后精确匹配数量为 {matches.Length}");
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

    private static BridgeProcess? MatchExact(
        IReadOnlyList<BridgeProcess> processes,
        Pm2BridgeProcessIdentity expected)
    {
        var sameName = processes.Where(process =>
            string.Equals(process.Name, expected.Name, StringComparison.Ordinal)).ToArray();
        return sameName.Length == 1 && sameName[0].PmId == expected.PmId &&
               SamePath(sameName[0].Cwd, expected.Cwd) && SamePath(sameName[0].Script, expected.Script) &&
               sameName[0].Arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal)
            ? sameName[0] : null;
    }

    private static IReadOnlyList<string> ReadArguments(JsonElement environment)
    {
        if (!environment.TryGetProperty("args", out var arguments) ||
            arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (arguments.ValueKind != JsonValueKind.Array) throw new JsonException("pm2_env.args 必须是数组");
        return arguments.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? string.Empty
            : throw new JsonException("pm2_env.args 项必须是字符串")).ToArray();
    }

    private static bool SafeRegistration(Pm2BridgeRegistration registration) =>
        registration.Name.Length is >= 1 and <= 120 &&
        char.IsAsciiLetterOrDigit(registration.Name[0]) &&
        registration.Name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        Path.IsPathFullyQualified(registration.Cwd) && Path.IsPathFullyQualified(registration.Script) &&
        registration.Cwd.Length <= 500 && registration.Script.Length <= 500 &&
        registration.Arguments.Count <= 64 &&
        registration.Arguments.All(static argument => argument.Length <= 1000 &&
            argument.All(static character => character is not '\0' and not '\r' and not '\n'));

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
