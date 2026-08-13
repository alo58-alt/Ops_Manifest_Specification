using System.Collections.Concurrent;
using System.Diagnostics;
using CompanyOps.Contracts;

namespace CompanyOps.SessionAgent;

public sealed class InteractiveProcessManager
{
    private readonly ConcurrentDictionary<string, ProcessRegistration> _processes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly Dictionary<string, PersistedRegistration> _persisted = new(StringComparer.Ordinal);

    public InteractiveProcessManager()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _statePath = Path.Combine(local, "CompanyOps", "SessionAgent", "processes.json");
        RestoreRegistrations();
    }

    public async Task<(bool Success, string? ErrorCode, string Detail)> ExecuteAsync(
        InteractiveClaim claim,
        InteractiveAppControlRequest request,
        CancellationToken cancellationToken)
    {
        if (!ClaimMatches(claim, request))
        {
            return (false, "ownership_changed", "请求中的 EXE、工作目录或参数与当前声明不一致");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = Key(claim);
            return request.Action switch
            {
                ComponentOperationAction.Start => Start(claim, key),
                ComponentOperationAction.Stop => await StopAsync(claim, key, cancellationToken),
                ComponentOperationAction.Restart => await RestartAsync(claim, key, cancellationToken),
                _ => (false, "invalid_action", "不支持的交互程序操作")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureLogonAppsStartedAsync(
        IReadOnlyList<InteractiveClaim> claims,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var claim in claims.Where(static item => item.StartPolicy == "userLogon"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Start(claim, Key(claim));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<InteractiveAppProcessSnapshot> Snapshot(IReadOnlyList<InteractiveClaim> claims) =>
        claims.Select(claim =>
        {
            var registration = LiveRegistration(Key(claim));
            return new InteractiveAppProcessSnapshot(
                claim.ProjectId,
                claim.Environment,
                claim.ComponentId,
                claim.Executable,
                claim.WorkingDirectory,
                claim.Arguments,
                registration is null ? "stopped" : "running",
                registration?.Process.Id,
                registration?.StartedAt);
        }).ToArray();

    private (bool Success, string? ErrorCode, string Detail) Start(InteractiveClaim claim, string key)
    {
        var current = LiveRegistration(key);
        if (current is not null)
        {
            return (true, null, $"已在当前用户 Session {Process.GetCurrentProcess().SessionId} 运行，PID {current.Process.Id}");
        }
        if (!File.Exists(claim.Executable) || !Directory.Exists(claim.WorkingDirectory))
        {
            return (false, "entrypoint_missing", "声明的 EXE 或工作目录不存在");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = claim.Executable,
            WorkingDirectory = claim.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (var argument in claim.Arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            var process = Process.Start(startInfo);
            if (process is null) return (false, "process_start_failed", "Windows 未返回新进程");
            var registration = new ProcessRegistration(process, DateTimeOffset.UtcNow);
            _processes[key] = registration;
            _persisted[key] = new PersistedRegistration(key, process.Id, registration.StartedAt);
            SaveRegistrations();
            return (true, null, $"已在当前登录用户会话启动，PID {process.Id}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, "process_start_failed", exception.Message);
        }
    }

    private async Task<(bool Success, string? ErrorCode, string Detail)> RestartAsync(
        InteractiveClaim claim,
        string key,
        CancellationToken cancellationToken)
    {
        var stopped = await StopAsync(claim, key, cancellationToken);
        return !stopped.Success ? stopped : Start(claim, key);
    }

    private async Task<(bool Success, string? ErrorCode, string Detail)> StopAsync(
        InteractiveClaim claim,
        string key,
        CancellationToken cancellationToken)
    {
        var registration = LiveRegistration(key);
        if (registration is null) return (true, null, "交互程序已停止");
        var process = registration.Process;
        try
        {
            var closeRequested = process.CloseMainWindow();
            if (closeRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(claim.StopTimeoutSeconds));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            if (!process.HasExited)
            {
                if (!claim.AllowForceTerminate)
                {
                    return (false, "graceful_close_failed", "程序未响应关闭请求；声明未授权强制终止");
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            _processes.TryRemove(key, out var removed);
            _persisted.Remove(key);
            removed?.Process.Dispose();
            SaveRegistrations();
            return (true, null, "已停止唯一归属的交互程序");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (false, "process_stop_failed", exception.Message);
        }
    }

    private ProcessRegistration? LiveRegistration(string key)
    {
        if (!_processes.TryGetValue(key, out var registration))
        {
            if (!_persisted.TryGetValue(key, out var item)) return null;
            try
            {
                var process = Process.GetProcessById(item.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime() != item.StartedAt.UtcDateTime)
                {
                    _persisted.Remove(key);
                    SaveRegistrations();
                    process.Dispose();
                    return null;
                }
                registration = new ProcessRegistration(process, item.StartedAt);
                _processes[key] = registration;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _persisted.Remove(key);
                SaveRegistrations();
                return null;
            }
        }
        try
        {
            if (!registration.Process.HasExited) return registration;
        }
        catch (InvalidOperationException) { }
        if (_processes.TryRemove(key, out var removed))
        {
            _persisted.Remove(key);
            removed.Process.Dispose();
            SaveRegistrations();
        }
        return null;
    }

    private static bool ClaimMatches(InteractiveClaim claim, InteractiveAppControlRequest request) =>
        string.Equals(claim.Executable, request.ExpectedExecutable, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(claim.WorkingDirectory, request.ExpectedWorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
        claim.Arguments.SequenceEqual(request.ExpectedArguments, StringComparer.Ordinal);

    private static string Key(InteractiveClaim claim) =>
        $"{claim.ProjectId}\n{claim.Environment}\n{claim.ComponentId}";

    private void RestoreRegistrations()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedRegistration[]>(File.ReadAllText(_statePath)) ?? [];
            foreach (var item in persisted) _persisted[item.Key] = item;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // A corrupt local tracking file is ignored; no unverified process is adopted.
        }
    }

    private void SaveRegistrations()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            foreach (var pair in _processes)
                _persisted[pair.Key] = new PersistedRegistration(pair.Key, pair.Value.Process.Id, pair.Value.StartedAt);
            var items = _persisted.Values.ToArray();
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(items));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Runtime control remains fail-closed even if the optional local tracking file cannot be persisted.
        }
    }

    private sealed record ProcessRegistration(Process Process, DateTimeOffset StartedAt);
    private sealed record PersistedRegistration(string Key, int ProcessId, DateTimeOffset StartedAt);
}
