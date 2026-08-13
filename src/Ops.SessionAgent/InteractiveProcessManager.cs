using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
            var registration = LiveRegistration(claim, Key(claim));
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
        var current = LiveRegistration(claim, key);
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
        var registration = LiveRegistration(claim, key);
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

    private ProcessRegistration? LiveRegistration(InteractiveClaim claim, string key)
    {
        if (!_processes.TryGetValue(key, out var registration))
        {
            if (_persisted.TryGetValue(key, out var item))
            {
                try
                {
                    var process = Process.GetProcessById(item.ProcessId);
                    if (process.HasExited || process.StartTime.ToUniversalTime() != item.StartedAt.UtcDateTime)
                    {
                        _persisted.Remove(key);
                        SaveRegistrations();
                        process.Dispose();
                    }
                    else
                    {
                        registration = new ProcessRegistration(process, item.StartedAt);
                        _processes[key] = registration;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _persisted.Remove(key);
                    SaveRegistrations();
                }
            }
        }

        if (registration is not null)
        {
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
        }

        return TryAdoptUniqueProcess(claim, key);
    }

    private ProcessRegistration? TryAdoptUniqueProcess(InteractiveClaim claim, string key)
    {
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var parentProcessIds = SnapshotParentProcessIds();
        var candidates = new List<(Process Process, InteractiveProcessCandidate Candidate)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (executable is null) { process.Dispose(); continue; }
                candidates.Add((process, new InteractiveProcessCandidate(
                    process.Id,
                    parentProcessIds.GetValueOrDefault(process.Id),
                    executable,
                    process.SessionId)));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                process.Dispose();
            }
        }

        var selectedId = SelectUniqueCandidate(
            candidates.Select(static item => item.Candidate),
            claim.Executable,
            currentSessionId);
        var selected = selectedId is null
            ? null
            : candidates.Single(item => item.Candidate.ProcessId == selectedId.Value).Process;
        foreach (var candidate in candidates.Where(item => item.Process.Id != selectedId))
            candidate.Process.Dispose();
        if (selected is null) return null;

        try
        {
            var startedAt = new DateTimeOffset(selected.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var adopted = new ProcessRegistration(selected, startedAt);
            _processes[key] = adopted;
            _persisted[key] = new PersistedRegistration(key, selected.Id, startedAt);
            SaveRegistrations();
            return adopted;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            selected.Dispose();
            return null;
        }
    }

    internal static int? SelectUniqueCandidate(
        IEnumerable<InteractiveProcessCandidate> candidates,
        string expectedExecutable,
        int expectedSessionId)
    {
        var matches = candidates.Where(candidate =>
            candidate.SessionId == expectedSessionId &&
            string.Equals(candidate.Executable, expectedExecutable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;

        var byId = matches.ToDictionary(static candidate => candidate.ProcessId);
        var roots = matches.Where(candidate => !byId.ContainsKey(candidate.ParentProcessId)).ToArray();
        if (roots.Length != 1) return null;
        var rootId = roots[0].ProcessId;
        foreach (var candidate in matches)
        {
            var current = candidate;
            var visited = new HashSet<int>();
            while (current.ProcessId != rootId)
            {
                if (!visited.Add(current.ProcessId) ||
                    !byId.TryGetValue(current.ParentProcessId, out var parent))
                    return null;
                current = parent;
            }
        }
        return rootId;
    }

    private static IReadOnlyDictionary<int, int> SnapshotParentProcessIds()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
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

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record InteractiveProcessCandidate(
    int ProcessId,
    int ParentProcessId,
    string Executable,
    int SessionId);
