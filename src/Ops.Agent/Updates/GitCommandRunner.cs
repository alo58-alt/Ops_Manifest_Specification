using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Updates;

public sealed record GitCommandResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string Detail => string.Join(
        " | ",
        new[] { StandardOutput.Trim(), StandardError.Trim() }
            .Where(static value => value.Length > 0));
}

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        GitCredentialHandle? credential = null);
}

public sealed class GitCommandRunner(IOptions<OpsOptions> options) : IGitCommandRunner
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly string _gitExecutable = ResolveGitExecutable(options.Value.GitExecutablePath);

    public async Task<GitCommandResult> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        GitCredentialHandle? credential = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        if (credential is not null)
        {
            startInfo.Environment["GIT_ASKPASS"] = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位 CompanyOps Agent 可执行文件");
            startInfo.Environment["GIT_ASKPASS_REQUIRE"] = "force";
            startInfo.Environment[GitCredentialAskPass.ModeEnvironmentVariable] = "1";
            startInfo.Environment[GitCredentialAskPass.FileEnvironmentVariable] = credential.FilePath;
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("credential.helper=");
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"safe.directory={repositoryRoot}");
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
                return new GitCommandResult(false, -1, string.Empty, "无法启动 Git");
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            return new GitCommandResult(
                process.ExitCode == 0,
                process.ExitCode,
                Limit(await stdout),
                Limit(await stderr));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return new GitCommandResult(false, -1, string.Empty, "Git 执行超时");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(false, -1, string.Empty, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveGitExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            if (!Path.GetFileName(fullPath).Equals("git.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                throw new InvalidOperationException("Ops:GitExecutablePath 必须指向存在的 git.exe");
            }
            return fullPath;
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git", "cmd", "git.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git", "bin", "git.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "git.exe";
    }

    private static string Limit(string value) =>
        value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters];

    private static void TryTerminate(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Only the exact Git child process is ever targeted.
        }
    }
}
