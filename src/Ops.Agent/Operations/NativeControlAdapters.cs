using System.Diagnostics;
using System.ServiceProcess;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Operations;

public sealed class WindowsServiceControlAdapter : IComponentControlAdapter
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public string Kind => "windowsService";

    public async Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !SafeNativeName(target.NativeId))
        {
            return new AdapterExecutionResult(false, "SCM 目标无效或当前不是 Windows");
        }

        var matches = ServiceController.GetServices()
            .Where(service => string.Equals(service.ServiceName, target.NativeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            foreach (var match in matches)
            {
                match.Dispose();
            }

            return new AdapterExecutionResult(false, $"SCM 精确名称匹配数量为 {matches.Length}");
        }

        using var service = matches[0];
        try
        {
            if (action is ComponentOperationAction.Stop or ComponentOperationAction.Restart)
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    await WaitForStatusAsync(service, ServiceControllerStatus.Stopped, cancellationToken);
                }
            }

            if (action is ComponentOperationAction.Start or ComponentOperationAction.Restart)
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    await WaitForStatusAsync(service, ServiceControllerStatus.Running, cancellationToken);
                }
            }

            return new AdapterExecutionResult(true, $"SCM {action} 完成");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.TimeoutException)
        {
            return new AdapterExecutionResult(false, exception.Message);
        }
    }

    private static async Task WaitForStatusAsync(
        ServiceController service,
        ServiceControllerStatus desired,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + OperationTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == desired)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new System.TimeoutException($"等待服务进入 {desired} 超时");
    }

    private static bool SafeNativeName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(static character => !char.IsControl(character));
}

public sealed class ScheduledTaskControlAdapter(FixedCommandRunner runner) : IComponentControlAdapter
{
    public string Kind => "scheduledTask";

    public async Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken)
    {
        if (!IsSafeTaskName(target.NativeId))
        {
            return new AdapterExecutionResult(false, "任务计划名称无效");
        }

        if (action is ComponentOperationAction.Stop or ComponentOperationAction.Restart)
        {
            var stop = await runner.RunAsync("schtasks.exe", ["/End", "/TN", target.NativeId], cancellationToken);
            if (!stop.Success && action == ComponentOperationAction.Stop)
            {
                return stop;
            }
        }

        return action is ComponentOperationAction.Start or ComponentOperationAction.Restart
            ? await runner.RunAsync("schtasks.exe", ["/Run", "/TN", target.NativeId], cancellationToken)
            : new AdapterExecutionResult(true, "任务已停止");
    }

    private static bool IsSafeTaskName(string value) =>
        value.StartsWith('\\') && value.Length <= 260 &&
        value.All(static character => !char.IsControl(character));
}

public sealed class IisSiteControlAdapter(FixedCommandRunner runner, string componentKind)
    : IComponentControlAdapter
{
    public string Kind => componentKind;

    public async Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken)
    {
        if (!SafeSiteName(target.NativeId))
        {
            return new AdapterExecutionResult(false, "IIS site 名称无效");
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appcmd = Path.Combine(windows, "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appcmd))
        {
            return new AdapterExecutionResult(false, "IIS appcmd.exe 不存在");
        }

        if (action is ComponentOperationAction.Stop or ComponentOperationAction.Restart)
        {
            var stop = await runner.RunAsync(
                appcmd,
                ["stop", "site", $"/site.name:{target.NativeId}"],
                cancellationToken);
            if (!stop.Success)
            {
                return stop;
            }
        }

        return action is ComponentOperationAction.Start or ComponentOperationAction.Restart
            ? await runner.RunAsync(
                appcmd,
                ["start", "site", $"/site.name:{target.NativeId}"],
                cancellationToken)
            : new AdapterExecutionResult(true, "IIS site 已停止");
    }

    private static bool SafeSiteName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
        value.All(static character => !char.IsControl(character));
}

public interface IPm2OwnerControlBridge
{
    Task<AdapterExecutionResult> ExecuteAsync(
        string pipeName,
        Pm2BridgeControlRequest request,
        CancellationToken cancellationToken);
}

public sealed class NamedPipePm2OwnerControlBridge(
    System.Text.Json.JsonSerializerOptions jsonOptions) : IPm2OwnerControlBridge
{
    public async Task<AdapterExecutionResult> ExecuteAsync(
        string pipeName,
        Pm2BridgeControlRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return new AdapterExecutionResult(false, "PM2 owner bridge 未配置");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions);
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            var response = line is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<Pm2BridgeControlResponse>(line, jsonOptions);
            return response?.Success == true
                ? new AdapterExecutionResult(true, response.Detail)
                : new AdapterExecutionResult(false, response?.Detail ?? "PM2 owner bridge 响应无效");
        }
        catch (Exception exception) when (
            exception is IOException or System.TimeoutException or OperationCanceledException)
        {
            return new AdapterExecutionResult(false, $"PM2 owner bridge 不可用：{exception.Message}");
        }
    }
}

public sealed class Pm2LegacyControlAdapter(
    IPm2OwnerControlBridge ownerBridge,
    CompanyOps.Agent.Inventory.ILegacyPm2ClaimProvider claimProvider) : IComponentControlAdapter
{
    public string Kind => "pm2Legacy";

    public async Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target,
        ComponentOperationAction action,
        CancellationToken cancellationToken)
    {
        if (target.PmId is not >= 0)
        {
            return new AdapterExecutionResult(false, "缺少已精确归属的 pm_id");
        }

        var claims = await claimProvider.GetClaimsAsync(cancellationToken);
        var matches = claims.Where(claim =>
            claim.ProjectId == target.ProjectId &&
            claim.Environment == target.Environment &&
            claim.ComponentId == target.ComponentId &&
            claim.BindingError is null).ToArray();
        if (matches.Length != 1)
        {
            return new AdapterExecutionResult(false, "PM2 项目归属声明不唯一或不完整");
        }

        var claim = matches[0];
        if (claim.ExpectedCwd is not { } expectedCwd ||
            claim.ExpectedScript is not { } expectedScript ||
            claim.ControlPipeName is not { } controlPipeName)
        {
            return new AdapterExecutionResult(false, "PM2 项目归属声明不唯一或不完整");
        }

        return await ownerBridge.ExecuteAsync(
            controlPipeName,
            new Pm2BridgeControlRequest(
                Pm2BridgeProtocol.Version,
                Guid.CreateVersion7().ToString(),
                target.PmId.Value,
                claim.ProcessName,
                expectedCwd,
                expectedScript,
                action),
            cancellationToken);
    }
}

public sealed class FixedCommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaximumOutputCharacters = 16_384;

    public async Task<AdapterExecutionResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
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
                return new AdapterExecutionResult(false, "无法启动固定系统工具");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            var detail = string.Join(" | ", new[] { stdout, stderr }.Where(static value => value.Length > 0));
            if (detail.Length > MaximumOutputCharacters)
            {
                detail = detail[..MaximumOutputCharacters];
            }

            return new AdapterExecutionResult(process.ExitCode == 0, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminateExactProcess(process);
            return new AdapterExecutionResult(false, "固定系统工具执行超时");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AdapterExecutionResult(false, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryTerminateExactProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The adapter already returns a timeout. Never target any process other than this exact child.
        }
    }
}
