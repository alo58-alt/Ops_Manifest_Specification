using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CompanyOps.Setup;

internal sealed record InstallResult(string InstallRoot, string DataRoot, string ConsoleUrl, bool WasUpgrade);

internal sealed record ExistingInstallation(string InstallRoot, string DataRoot);

internal sealed record PayloadManifest(int Version, IReadOnlyList<PayloadManifestFile> Files);

internal sealed record PayloadManifestFile(string Path, string Sha256);

internal sealed class InstallerEngine
{
    private const string AgentServiceName = "CompanyOps.Agent";
    private const string ConsoleServiceName = "CompanyOps.Console";
    private const string ConsoleUrl = "http://127.0.0.1:19310/";
    private static readonly TimeSpan DirectorySwitchTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly string[] ProductComponents = ["Agent", "Console", "Pm2Bridge", "SessionAgent", "Cli"];

    internal static ExistingInstallation? DetectExistingInstallation()
    {
        var agentImagePath = ReadServiceImagePath(AgentServiceName);
        var consoleImagePath = ReadServiceImagePath(ConsoleServiceName);
        if (agentImagePath is null && consoleImagePath is null)
        {
            return null;
        }
        if (agentImagePath is null || consoleImagePath is null)
        {
            throw new InvalidOperationException("CompanyOps 安装不完整：Agent 与 Console 服务没有同时存在，请先排障。 ");
        }

        var agentExe = ExtractExecutablePath(agentImagePath);
        var consoleExe = ExtractExecutablePath(consoleImagePath);
        var installRoot = Directory.GetParent(Path.GetDirectoryName(agentExe)!)?.FullName
            ?? throw new InvalidOperationException("无法识别 CompanyOps 程序目录。 ");
        var consoleRoot = Directory.GetParent(Path.GetDirectoryName(consoleExe)!)?.FullName;
        if (!string.Equals(installRoot, consoleRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(agentExe), "CompanyOps.Agent.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(consoleExe), "CompanyOps.Console.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CompanyOps 服务入口与标准安装结构不一致，拒绝自动升级。 ");
        }

        var settingsPath = Path.Combine(installRoot, "Agent", "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            throw new InvalidOperationException("无法读取现有 CompanyOps Agent 配置。 ");
        }
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var manifestDirectory = settings.RootElement
            .GetProperty("Ops")
            .GetProperty("ManifestDirectory")
            .GetString();
        var dataRoot = manifestDirectory is null
            ? null
            : Directory.GetParent(Path.TrimEndingDirectorySeparator(manifestDirectory))?.FullName;
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new InvalidOperationException("无法从现有 Agent 配置识别 CompanyOps 数据目录。 ");
        }

        return new ExistingInstallation(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)));
    }

    internal static void VerifyPackagePayload(string? packageRoot = null)
    {
        var resolvedPackageRoot = ResolvePackageRoot(packageRoot);
        var payloadRoot = Path.Combine(resolvedPackageRoot, "Payload");
        var manifestPath = Path.Combine(resolvedPackageRoot, "Payload.sha256.json");
        ValidateExternalPayload(payloadRoot, manifestPath);
        ValidatePayload(payloadRoot);
    }

    public InstallResult InstallOrUpdate(string installInput, string dataInput, IProgress<string> progress)
    {
        var installRoot = ValidateLocalDirectory(installInput, "程序目录");
        var dataRoot = ValidateLocalDirectory(dataInput, "数据目录");
        ValidateIndependentRoots(installRoot, dataRoot);
        EnsureAdministrator();
        var existing = DetectExistingInstallation();
        if (existing is not null)
        {
            if (!string.Equals(existing.InstallRoot, installRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.DataRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"输入目录与现有安装不一致。\n程序目录应为：{existing.InstallRoot}\n数据目录应为：{existing.DataRoot}");
            }
            return Upgrade(existing, progress);
        }

        return InstallFirstTime(installRoot, dataRoot, progress);
    }

    private InstallResult InstallFirstTime(string installRoot, string dataRoot, IProgress<string> progress)
    {
        EnsureFirstInstallTargets(installRoot, dataRoot);
        EnsureServiceMissing(AgentServiceName);
        EnsureServiceMissing(ConsoleServiceName);

        progress.Report("正在校验安装包…");
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"CompanyOps.Setup.{Environment.ProcessId}.{Guid.NewGuid():N}");
        var installCreated = false;
        var dataCreated = false;
        var agentCreated = false;
        var consoleCreated = false;
        try
        {
            CopyVerifiedPackagePayload(stagingRoot);
            ValidatePayload(stagingRoot);

            progress.Report("正在复制程序文件…");
            Directory.CreateDirectory(installRoot);
            installCreated = true;
            foreach (var component in ProductComponents)
            {
                CopyDirectory(
                    Path.Combine(stagingRoot, component),
                    Path.Combine(installRoot, component));
            }
            ApplyDirectoryAcl(installRoot, "*S-1-5-32-544:(OI)(CI)F");
            ApplyDirectoryAcl(installRoot, "*S-1-5-18:(OI)(CI)F");
            ApplyDirectoryAcl(installRoot, "*S-1-5-20:(OI)(CI)RX");

            progress.Report("正在创建数据目录…");
            Directory.CreateDirectory(dataRoot);
            dataCreated = true;
            var manifestDirectory = Path.Combine(dataRoot, "manifests");
            var agentStateDirectory = Path.Combine(dataRoot, "Agent");
            var snapshotDirectory = Path.Combine(agentStateDirectory, "pm2-snapshots");
            var interactiveSnapshotDirectory = Path.Combine(agentStateDirectory, "interactive-snapshots");
            Directory.CreateDirectory(manifestDirectory);
            Directory.CreateDirectory(agentStateDirectory);
            Directory.CreateDirectory(snapshotDirectory);
            Directory.CreateDirectory(interactiveSnapshotDirectory);
            ApplyDirectoryAcl(dataRoot, "*S-1-5-18:(OI)(CI)F");
            progress.Report("正在写入主机配置…");
            WriteSettings(installRoot, manifestDirectory, agentStateDirectory, snapshotDirectory, interactiveSnapshotDirectory);

            progress.Report("正在注册 Windows 服务…");
            var agentExe = Path.Combine(installRoot, "Agent", "CompanyOps.Agent.exe");
            var consoleExe = Path.Combine(installRoot, "Console", "CompanyOps.Console.exe");
            RunSc(
                "create",
                AgentServiceName,
                "binPath=", Quote(agentExe),
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", "CompanyOps Agent");
            agentCreated = true;
            RunSc(
                "description",
                AgentServiceName,
                "Windows 多项目统一运维 Agent");
            RunSc(
                "failure",
                AgentServiceName,
                "reset=", "86400",
                "actions=", "restart/5000/restart/15000/none/0");

            RunSc(
                "create",
                ConsoleServiceName,
                "binPath=", Quote(consoleExe),
                "start=", "auto",
                "obj=", @"NT AUTHORITY\NetworkService",
                "DisplayName=", "CompanyOps Console");
            consoleCreated = true;
            RunSc("config", ConsoleServiceName, "depend=", AgentServiceName);
            RunSc(
                "description",
                ConsoleServiceName,
                "本机 CompanyOps 管理 Console");
            RunSc(
                "failure",
                ConsoleServiceName,
                "reset=", "86400",
                "actions=", "restart/5000/restart/15000/none/0");

            progress.Report("正在启动 Agent…");
            RunSc("start", AgentServiceName);
            WaitForService(AgentServiceName, "RUNNING", TimeSpan.FromSeconds(30));

            progress.Report("正在启动 Console…");
            RunSc("start", ConsoleServiceName);
            WaitForService(ConsoleServiceName, "RUNNING", TimeSpan.FromSeconds(30));
            WaitForConsole(installRoot, TimeSpan.FromSeconds(30));

            progress.Report("正在启用当前用户的 GUI 项目会话代理…");
            RegisterAndStartSessionAgent(installRoot);

            progress.Report("安装完成");
            return new InstallResult(installRoot, dataRoot, ConsoleUrl, false);
        }
        catch (Exception exception)
        {
            UnregisterSessionAgent(installRoot);
            var rollback = Rollback(
                installRoot,
                dataRoot,
                installCreated,
                dataCreated,
                agentCreated,
                consoleCreated);
            throw new InvalidOperationException(
                $"{exception.Message}\n\n安装未完成，已执行失败恢复：{rollback}",
                exception);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private InstallResult Upgrade(ExistingInstallation existing, IProgress<string> progress)
    {
        progress.Report("正在校验升级包…");
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"CompanyOps.Setup.Upgrade.{Environment.ProcessId}.{Guid.NewGuid():N}");
        var transactionId = Guid.NewGuid().ToString("N");
        var preparedRoot = Path.Combine(existing.InstallRoot, $".upgrade-new-{transactionId}");
        var backupRoot = Path.Combine(existing.InstallRoot, $".upgrade-backup-{transactionId}");
        var servicesStopped = false;
        var switchedComponents = new List<string>();
        try
        {
            CopyVerifiedPackagePayload(stagingRoot);
            ValidatePayload(stagingRoot);
            // An older installation may have been created by another administrator.
            // Use the well-known Administrators SID so upgrades stay locale independent.
            ApplyDirectoryAcl(existing.InstallRoot, "*S-1-5-32-544:(OI)(CI)F");
            Directory.CreateDirectory(preparedRoot);
            foreach (var component in ProductComponents)
            {
                CopyDirectory(Path.Combine(stagingRoot, component), Path.Combine(preparedRoot, component));
            }

            progress.Report("正在保留现有主机配置…");
            foreach (var relativeConfig in new[] { @"Agent\appsettings.json", @"Console\appsettings.json" })
            {
                var currentConfig = Path.Combine(existing.InstallRoot, relativeConfig);
                var preparedConfig = Path.Combine(preparedRoot, relativeConfig);
                if (!File.Exists(currentConfig))
                {
                    throw new InvalidOperationException($"现有安装缺少配置：{relativeConfig}");
                }
                File.Copy(currentConfig, preparedConfig, overwrite: true);
            }

            ApplyDirectoryAcl(preparedRoot, "*S-1-5-18:(OI)(CI)F");
            ApplyDirectoryAcl(preparedRoot, "*S-1-5-20:(OI)(CI)RX");
            Directory.CreateDirectory(backupRoot);

            progress.Report("正在停止 CompanyOps 自身服务…");
            StopSessionAgentIfRunning(existing.InstallRoot);
            StopServiceIfRunning(ConsoleServiceName);
            StopServiceIfRunning(AgentServiceName);
            servicesStopped = true;

            progress.Report("正在切换 CompanyOps 新版本…");
            foreach (var component in ProductComponents)
            {
                var current = Path.Combine(existing.InstallRoot, component);
                var backup = Path.Combine(backupRoot, component);
                var prepared = Path.Combine(preparedRoot, component);
                if (!Directory.Exists(prepared))
                {
                    throw new InvalidOperationException($"升级目录不完整：{component}");
                }
                if (!Directory.Exists(current))
                {
                    MoveDirectoryWithRetry(prepared, current, component, DirectorySwitchTimeout);
                    switchedComponents.Add(component);
                    continue;
                }
                MoveDirectoryWithRetry(current, backup, component, DirectorySwitchTimeout);
                var switchedCurrent = false;
                try
                {
                    MoveDirectoryWithRetry(prepared, current, component, DirectorySwitchTimeout);
                    switchedComponents.Add(component);
                    switchedCurrent = true;
                }
                catch
                {
                    if (!switchedCurrent && !Directory.Exists(current) && Directory.Exists(backup))
                    {
                        MoveDirectoryWithRetry(backup, current, component, DirectorySwitchTimeout);
                    }
                    throw;
                }
            }

            progress.Report("正在启动升级后的 Agent…");
            RunSc("start", AgentServiceName);
            WaitForService(AgentServiceName, "RUNNING", TimeSpan.FromSeconds(30));
            progress.Report("正在启动升级后的 Console…");
            RunSc("start", ConsoleServiceName);
            WaitForService(ConsoleServiceName, "RUNNING", TimeSpan.FromSeconds(30));
            WaitForConsole(existing.InstallRoot, TimeSpan.FromSeconds(30));
            ConfigureSessionAgentFromAgentSettings(existing.InstallRoot);
            RegisterAndStartSessionAgent(existing.InstallRoot);

            var backupRemoved = TryDeleteDirectory(backupRoot);
            if (!backupRemoved)
            {
                progress.Report($"升级成功；旧版本临时目录稍后可清理：{backupRoot}");
            }
            TryDeleteDirectory(preparedRoot);
            if (backupRemoved)
            {
                progress.Report("升级完成");
            }
            return new InstallResult(existing.InstallRoot, existing.DataRoot, ConsoleUrl, true);
        }
        catch (Exception exception)
        {
            var recovery = servicesStopped
                ? RestoreUpgrade(existing.InstallRoot, preparedRoot, backupRoot, switchedComponents)
                : "尚未切换运行版本";
            throw new InvalidOperationException(
                $"{exception.Message}\n\n升级未完成，失败恢复：{recovery}",
                exception);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(preparedRoot);
        }
    }

    private static string RestoreUpgrade(
        string installRoot,
        string preparedRoot,
        string backupRoot,
        IReadOnlyCollection<string> switchedComponents)
    {
        RunProcess("sc.exe", ["stop", ConsoleServiceName], throwOnFailure: false);
            RunProcess("sc.exe", ["stop", AgentServiceName], throwOnFailure: false);
        try
        {
            WaitForService(ConsoleServiceName, "STOPPED", TimeSpan.FromSeconds(15));
        }
        catch { }
        try
        {
            WaitForService(AgentServiceName, "STOPPED", TimeSpan.FromSeconds(15));
        }
        catch { }

        var errors = new List<string>();
        foreach (var component in switchedComponents.Reverse())
        {
            var current = Path.Combine(installRoot, component);
            var failed = Path.Combine(preparedRoot, component + ".failed");
            var backup = Path.Combine(backupRoot, component);
            try
            {
                if (Directory.Exists(current))
                {
                    MoveDirectoryWithRetry(current, failed, component, DirectorySwitchTimeout);
                }
                if (Directory.Exists(backup))
                {
                    MoveDirectoryWithRetry(backup, current, component, DirectorySwitchTimeout);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{component}: {exception.Message}");
            }
        }

        foreach (var component in ProductComponents.Except(switchedComponents, StringComparer.OrdinalIgnoreCase))
        {
            var current = Path.Combine(installRoot, component);
            var backup = Path.Combine(backupRoot, component);
            if (Directory.Exists(current) || !Directory.Exists(backup))
            {
                continue;
            }
            try
            {
                MoveDirectoryWithRetry(backup, current, component, DirectorySwitchTimeout);
            }
            catch (Exception exception)
            {
                errors.Add($"{component}: {exception.Message}");
            }
        }

        if (errors.Count == 0)
        {
            var agent = RunProcess("sc.exe", ["start", AgentServiceName], throwOnFailure: false);
            var console = RunProcess("sc.exe", ["start", ConsoleServiceName], throwOnFailure: false);
            if (agent.ExitCode == 0 && console.ExitCode == 0)
            {
                TryDeleteDirectory(backupRoot);
                return "旧版本已恢复并重新启动";
            }
            errors.Add("旧版本目录已恢复，但服务重新启动失败，请检查 Windows 服务状态");
        }
        return string.Join("；", errors);
    }

    private static void StopServiceIfRunning(string serviceName)
    {
        var query = RunProcess("sc.exe", ["queryex", serviceName], throwOnFailure: false);
        if (query.ExitCode != 0)
        {
            throw new InvalidOperationException($"无法查询服务 {serviceName}：{query.Output.Trim()}");
        }
        if (query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process? serviceProcess = null;
        var serviceProcessId = TryParseServiceProcessId(query.Output);
        if (serviceProcessId is > 0)
        {
            try
            {
                serviceProcess = Process.GetProcessById(serviceProcessId.Value);
            }
            catch (ArgumentException)
            {
                // The process exited between queryex and opening the process handle.
            }
        }

        try
        {
            RunSc("stop", serviceName);
            WaitForService(serviceName, "STOPPED", TimeSpan.FromSeconds(30));
            if (serviceProcess is not null &&
                !serviceProcess.HasExited &&
                !serviceProcess.WaitForExit((int)TimeSpan.FromSeconds(20).TotalMilliseconds))
            {
                throw new InvalidOperationException(
                    $"服务 {serviceName} 已报告 STOPPED，但旧进程 PID {serviceProcess.Id} 尚未退出，拒绝切换程序目录。");
            }
        }
        finally
        {
            serviceProcess?.Dispose();
        }
    }

    internal static int? TryParseServiceProcessId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*PID\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var processId))
            {
                return processId;
            }
        }

        return null;
    }

    internal static void MoveDirectoryWithRetry(
        string source,
        string destination,
        string component,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        do
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(250);
            }
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException(
            $"无法切换 CompanyOps 组件目录 {component}。Windows 在等待服务进程退出后仍拒绝访问：{source}\n" +
            "旧版本尚未被覆盖，安装程序将自动恢复。请不要手工删除目录。",
            last);
    }

    private static string ValidateLocalDirectory(string input, string label)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException($"请选择{label}。");
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim()));
        var root = Path.GetPathRoot(fullPath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            string.IsNullOrWhiteSpace(root) ||
            !root.EndsWith(@":\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label}必须是本机磁盘绝对路径，不能使用相对路径或网络路径。");
        }

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"{label}所在磁盘不存在：{root}");
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不能把磁盘根目录作为{label}。");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void ValidateIndependentRoots(string installRoot, string dataRoot)
    {
        var installPrefix = installRoot + Path.DirectorySeparatorChar;
        var dataPrefix = dataRoot + Path.DirectorySeparatorChar;
        if (string.Equals(installRoot, dataRoot, StringComparison.OrdinalIgnoreCase) ||
            installPrefix.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase) ||
            dataPrefix.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("程序目录和数据目录必须相互独立，不能相同或互相嵌套。");
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("安装程序需要管理员权限，请在 UAC 提示中选择“是”。");
        }
    }

    private static void EnsureFirstInstallTargets(string installRoot, string dataRoot)
    {
        if (Directory.Exists(installRoot) || File.Exists(installRoot))
        {
            throw new InvalidOperationException($"程序目录已存在。首次安装拒绝覆盖：{installRoot}");
        }

        if (Directory.Exists(dataRoot) || File.Exists(dataRoot))
        {
            throw new InvalidOperationException($"数据目录已存在。首次安装拒绝覆盖：{dataRoot}");
        }
    }

    private static void EnsureServiceMissing(string serviceName)
    {
        var result = RunProcess("sc.exe", ["query", serviceName], throwOnFailure: false);
        if (result.ExitCode == 0)
        {
            throw new InvalidOperationException($"Windows 服务已存在：{serviceName}。请使用升级或修复流程。");
        }

        if (!result.Output.Contains("1060", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("未安装", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"无法确认服务 {serviceName} 是否存在：{result.Output.Trim()}");
        }
    }

    private static string? ReadServiceImagePath(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        return key?.GetValue("ImagePath") as string;
    }

    private static string ExtractExecutablePath(string imagePath)
    {
        var value = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing <= 1)
            {
                throw new InvalidOperationException("Windows 服务入口引号不完整。 ");
            }
            return Path.GetFullPath(value[1..closing]);
        }
        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0)
        {
            throw new InvalidOperationException("Windows 服务入口不是 EXE。 ");
        }
        return Path.GetFullPath(value[..(exeEnd + 4)]);
    }

    private static void CopyVerifiedPackagePayload(string stagingRoot)
    {
        var packageRoot = ResolvePackageRoot(null);
        var payloadRoot = Path.Combine(packageRoot, "Payload");
        var manifestPath = Path.Combine(packageRoot, "Payload.sha256.json");
        ValidateExternalPayload(payloadRoot, manifestPath);
        CopyDirectory(payloadRoot, stagingRoot);
    }

    private static string ResolvePackageRoot(string? packageRoot)
    {
        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        }
        var setupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        return Directory.GetParent(setupDirectory)?.FullName ??
            throw new InvalidOperationException("无法识别安装包根目录。");
    }

    private static void ValidateExternalPayload(string payloadRoot, string manifestPath)
    {
        if (!Directory.Exists(payloadRoot) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException("安装包不完整：Payload 目录或哈希清单不存在。请完整解压 ZIP 后再运行安装程序。");
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(payloadRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"安装包包含不允许的重解析点：{Path.GetRelativePath(payloadRoot, path)}");
            }
        }

        var manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath)) ??
            throw new InvalidOperationException("安装包哈希清单无法解析。");
        if (manifest.Version != 1 || manifest.Files.Count == 0)
        {
            throw new InvalidOperationException("安装包哈希清单版本无效或没有文件。");
        }

        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot)) + Path.DirectorySeparatorChar;
        foreach (var item in manifest.Files)
        {
            var relativePath = item.Path.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
                item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"安装包哈希清单包含无效条目：{item.Path}");
            }
            var fullPath = Path.GetFullPath(Path.Combine(payloadRoot, relativePath));
            if (!fullPath.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase) ||
                !expectedPaths.Add(Path.GetRelativePath(payloadRoot, fullPath).Replace('\\', '/')) ||
                !File.Exists(fullPath))
            {
                throw new InvalidOperationException($"安装包哈希清单包含不安全、重复或缺失的文件：{item.Path}");
            }
            using var stream = File.OpenRead(fullPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(item.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"安装包文件校验失败：{item.Path}");
            }
        }

        var actualPaths = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(expectedPaths))
        {
            throw new InvalidOperationException("安装包 Payload 与哈希清单文件集合不一致。");
        }
    }

    private static void ValidatePayload(string stagingRoot)
    {
        foreach (var relativePath in new[]
                 {
                     @"Agent\CompanyOps.Agent.exe",
                     @"Console\CompanyOps.Console.exe",
                     @"Cli\companyops.exe",
                     @"Pm2Bridge\CompanyOps.Pm2Bridge.exe",
                     @"SessionAgent\CompanyOps.SessionAgent.exe"
                 })
        {
            if (!File.Exists(Path.Combine(stagingRoot, relativePath)))
            {
                throw new InvalidOperationException($"安装包不完整，缺少：{relativePath}");
            }
        }
    }

    private static void WriteSettings(
        string installRoot,
        string manifestDirectory,
        string agentStateDirectory,
        string snapshotDirectory,
        string interactiveSnapshotDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法识别当前安装用户 SID。");
        var ownerKey = OwnerKey(ownerSid);
        var agentSettings = new
        {
            Ops = new
            {
                HostId = Environment.MachineName,
                ManifestDirectory = manifestDirectory,
                StateDirectory = agentStateDirectory,
                Pm2SnapshotDirectory = snapshotDirectory,
                InteractiveSnapshotDirectory = interactiveSnapshotDirectory,
                PipeName = "CompanyOps.Agent.v1",
                InventoryIntervalSeconds = 30,
                EnableMutations = false,
                EnableExistingServiceOperations = true,
                EnableInteractiveSessionOperations = true,
                EnableExistingGitUpdates = true,
                GitExecutablePath = string.Empty,
                AllowedProjectInstallRoots = Array.Empty<string>(),
                AllowedClientSids = new[] { "S-1-5-20" }
            },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Information", ["Microsoft.Hosting.Lifetime"] = "Information" } }
        };
        var currentUser = WindowsIdentity.GetCurrent().Name;
        var consoleSettings = new
        {
            Urls = "http://127.0.0.1:19310",
            Console = new
            {
                PipeName = "CompanyOps.Agent.v1",
                Operators = new[] { currentUser },
                Administrators = Array.Empty<string>(),
                AllowLocalAdministrators = true
            },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Information", ["Microsoft.AspNetCore"] = "Warning" } }
        };
        File.WriteAllText(
            Path.Combine(installRoot, "Agent", "appsettings.json"),
            JsonSerializer.Serialize(agentSettings, JsonOptions));
        File.WriteAllText(
            Path.Combine(installRoot, "Console", "appsettings.json"),
            JsonSerializer.Serialize(consoleSettings, JsonOptions));
        var sessionSettings = new
        {
            SessionAgent = new
            {
                ManifestDirectory = manifestDirectory,
                SnapshotDirectory = interactiveSnapshotDirectory,
                PipeName = $"CompanyOps.SessionAgent.{ownerKey}",
                SnapshotIntervalSeconds = 10
            },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Information", ["Microsoft.Hosting.Lifetime"] = "Information" } }
        };
        File.WriteAllText(
            Path.Combine(installRoot, "SessionAgent", "appsettings.json"),
            JsonSerializer.Serialize(sessionSettings, JsonOptions));
        ApplyDirectoryAcl(manifestDirectory, $"*{ownerSid}:(OI)(CI)RX");
        ApplyDirectoryAcl(interactiveSnapshotDirectory, $"*{ownerSid}:(OI)(CI)M");
    }

    private static void ConfigureSessionAgentFromAgentSettings(string installRoot)
    {
        var agentSettingsPath = Path.Combine(installRoot, "Agent", "appsettings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(agentSettingsPath));
        var ops = settings.RootElement.GetProperty("Ops");
        var manifestDirectory = ops.GetProperty("ManifestDirectory").GetString()
            ?? throw new InvalidOperationException("Agent 配置缺少 ManifestDirectory。");
        var stateDirectory = ops.GetProperty("StateDirectory").GetString()
            ?? throw new InvalidOperationException("Agent 配置缺少 StateDirectory。");
        var snapshotDirectory = Path.Combine(stateDirectory, "interactive-snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        var mutableSettings = JsonNode.Parse(File.ReadAllText(agentSettingsPath))?.AsObject()
            ?? throw new InvalidOperationException("Agent 配置不是有效 JSON。");
        mutableSettings["Ops"]!["InteractiveSnapshotDirectory"] = snapshotDirectory;
        mutableSettings["Ops"]!["EnableInteractiveSessionOperations"] = true;
        File.WriteAllText(agentSettingsPath, mutableSettings.ToJsonString(JsonOptions));
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法识别当前安装用户 SID。");
        var sessionSettings = new
        {
            SessionAgent = new
            {
                ManifestDirectory = manifestDirectory,
                SnapshotDirectory = snapshotDirectory,
                PipeName = $"CompanyOps.SessionAgent.{OwnerKey(ownerSid)}",
                SnapshotIntervalSeconds = 10
            },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Information", ["Microsoft.Hosting.Lifetime"] = "Information" } }
        };
        File.WriteAllText(Path.Combine(installRoot, "SessionAgent", "appsettings.json"), JsonSerializer.Serialize(sessionSettings, JsonOptions));
        ApplyDirectoryAcl(manifestDirectory, $"*{ownerSid}:(OI)(CI)RX");
        ApplyDirectoryAcl(snapshotDirectory, $"*{ownerSid}:(OI)(CI)M");
    }

    private static void RegisterAndStartSessionAgent(string installRoot)
    {
        var executable = Path.Combine(installRoot, "SessionAgent", "CompanyOps.SessionAgent.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("安装结果缺少 CompanyOps.SessionAgent.exe。");
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("无法识别当前安装用户 SID。");
        var taskName = $@"\CompanyOps\SessionAgent-{OwnerKey(ownerSid)}";
        StopSessionAgentIfRunning(installRoot);
        RunProcess("schtasks.exe", ["/Delete", "/TN", taskName, "/F"], throwOnFailure: false);
        var created = RunProcess(
            "schtasks.exe",
            ["/Create", "/TN", taskName, "/SC", "ONLOGON", "/TR", Quote(executable),
                "/RU", identity.Name, "/RL", "LIMITED", "/IT", "/F"],
            throwOnFailure: false);
        if (created.ExitCode != 0)
            throw new InvalidOperationException($"无法注册当前用户的 Session Agent 登录任务：{created.Output.Trim()}");
        var started = RunProcess("schtasks.exe", ["/Run", "/TN", taskName], throwOnFailure: false);
        if (started.ExitCode != 0)
            throw new InvalidOperationException($"Session Agent 登录任务已注册，但当前会话启动失败：{started.Output.Trim()}");
        Thread.Sleep(1000);
        if (!Process.GetProcessesByName("CompanyOps.SessionAgent").Any(process =>
            ProcessPathEquals(process, executable)))
            throw new InvalidOperationException("Session Agent 任务已运行，但未找到当前用户会话进程。");
    }

    private static void UnregisterSessionAgent(string installRoot)
    {
        StopSessionAgentIfRunning(installRoot);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
            RunProcess("schtasks.exe", ["/Delete", "/TN", $@"\CompanyOps\SessionAgent-{OwnerKey(identity.User.Value)}", "/F"], throwOnFailure: false);
    }

    private static void StopSessionAgentIfRunning(string installRoot)
    {
        var expected = Path.GetFullPath(Path.Combine(installRoot, "SessionAgent", "CompanyOps.SessionAgent.exe"));
        foreach (var process in Process.GetProcessesByName("CompanyOps.SessionAgent"))
        {
            using (process)
            {
                try
                {
                    if (!ProcessPathEquals(process, expected)) continue;
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(10_000)) throw new InvalidOperationException($"Session Agent PID {process.Id} 未退出。");
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static bool ProcessPathEquals(Process process, string expected)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string OwnerKey(string ownerSid)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ownerSid.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"安装包缺少目录：{Path.GetFileName(source)}");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void ApplyDirectoryAcl(string path, string grant)
    {
        var result = RunProcess(
            "icacls.exe",
            [path, "/grant", grant, "/T", "/C", "/Q"],
            throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"无法为 CompanyOps 服务账户配置目录权限：{path}\n{result.Output.Trim()}");
        }
    }

    private static string Rollback(
        string installRoot,
        string dataRoot,
        bool installCreated,
        bool dataCreated,
        bool agentCreated,
        bool consoleCreated)
    {
        var results = new List<string>();
        if (consoleCreated)
        {
            TryStopAndDeleteService(ConsoleServiceName, results);
        }

        if (agentCreated)
        {
            TryStopAndDeleteService(AgentServiceName, results);
        }

        if (installCreated)
        {
            results.Add(TryDeleteDirectory(installRoot) ? "程序目录已移除" : "程序目录需要人工检查");
        }

        if (dataCreated)
        {
            results.Add(TryDeleteDirectory(dataRoot) ? "本次新建数据目录已移除" : "数据目录需要人工检查");
        }

        return results.Count == 0 ? "没有产生主机变更" : string.Join("；", results);
    }

    private static void TryStopAndDeleteService(string serviceName, ICollection<string> results)
    {
        RunProcess("sc.exe", ["stop", serviceName], throwOnFailure: false);
        try
        {
            WaitForService(serviceName, "STOPPED", TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Continue with delete. SCM may keep it marked for deletion until the process exits.
        }
        var deleted = RunProcess("sc.exe", ["delete", serviceName], throwOnFailure: false);
        results.Add(deleted.ExitCode == 0 ? $"{serviceName} 已移除" : $"{serviceName} 需要人工检查");
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForService(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var result = RunProcess("sc.exe", ["query", serviceName], throwOnFailure: false);
            if (result.ExitCode == 0 && result.Output.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException($"服务 {serviceName} 未在限定时间内进入 {expectedState} 状态。");
    }

    private static void WaitForConsole(string installRoot, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        var expectedIndexPath = Path.Combine(installRoot, "Console", "wwwroot", "index.html");
        if (!File.Exists(expectedIndexPath))
        {
            throw new InvalidOperationException($"Console 发布目录缺少 index.html：{expectedIndexPath}");
        }
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedIndexPath)));
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        do
        {
            try
            {
                var probeUrl = $"{ConsoleUrl}?companyops-version={Guid.NewGuid():N}";
                using var response = client.GetAsync(probeUrl).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    var actualBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
                    if (string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    last = new InvalidDataException(
                        $"19310 返回的页面不是本次安装版本（预期 {expectedHash[..12]}，实际 {actualHash[..12]}）");
                }
                else
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException($"Console 本机版本验收失败：{last?.Message ?? "HTTP 服务未就绪"}");
    }

    private static void RunSc(params string[] arguments)
    {
        var result = RunProcess("sc.exe", arguments, throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows 服务操作失败：sc.exe {string.Join(' ', arguments)}\n{result.Output.Trim()}");
        }
    }

    private static ProcessResult RunProcess(
        string fileName,
        IEnumerable<string> arguments,
        bool throwOnFailure)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort for the installer-owned helper process only.
            }
            throw new TimeoutException($"命令执行超时：{fileName}");
        }

        Task.WaitAll(standardOutput, standardError);
        var result = new ProcessResult(
            process.ExitCode,
            standardOutput.Result + Environment.NewLine + standardError.Result);
        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"命令执行失败：{fileName}\n{result.Output.Trim()}");
        }

        return result;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private sealed record ProcessResult(int ExitCode, string Output);
}
