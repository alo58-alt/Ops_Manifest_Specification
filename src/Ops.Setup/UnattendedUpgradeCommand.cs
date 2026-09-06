using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompanyOps.Setup;

internal sealed record UnattendedUpgradeRequest(
    string FromRevision, string ToRevision, string InstallRoot, string DataRoot, bool Apply);

internal sealed record UnattendedUpgradeResult(
    string Outcome, string FromRevision, string ToRevision, string InstallRoot, string DataRoot,
    bool EnableMutations, string[] AllowedProjectInstallRoots, string? ConsoleUrl = null);

internal static class UnattendedUpgradeCommand
{
    internal static int Run(string[] args, TextWriter output, TextWriter error,
        Func<UnattendedUpgradeRequest, IProgress<string>, UnattendedUpgradeResult>? execute = null)
    {
        try
        {
            var request = Parse(args);
            var result = (execute ?? Execute)(request, new TextProgress(error));
            output.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"invalid_upgrade_arguments: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            error.WriteLine($"upgrade_failed: {exception.Message}");
            return 1;
        }
    }

    internal static UnattendedUpgradeRequest Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "--upgrade-unattended")
            throw new ArgumentException("必须显式选择 --upgrade-unattended。");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--apply")
            {
                if (apply) throw new ArgumentException("不能重复指定 --apply。");
                apply = true;
                continue;
            }
            if (name is not ("--from-revision" or "--to-revision" or "--install-root" or "--data-root") ||
                index + 1 >= args.Length || !values.TryAdd(name, args[++index]))
                throw new ArgumentException("参数缺失、重复或不在升级白名单中。");
        }
        foreach (var name in new[] { "--from-revision", "--to-revision", "--install-root", "--data-root" })
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"缺少 {name}。");
        foreach (var name in new[] { "--from-revision", "--to-revision" })
            if (!Regex.IsMatch(values[name], "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant))
                throw new ArgumentException($"{name} 必须是完整的源码提交哈希。");
        foreach (var name in new[] { "--install-root", "--data-root" })
            if (!Path.IsPathFullyQualified(values[name]) || !Regex.IsMatch(values[name], @"^[A-Za-z]:[\\/]"))
                throw new ArgumentException($"{name} 必须是本机磁盘绝对路径。");
        return new UnattendedUpgradeRequest(values["--from-revision"].ToLowerInvariant(),
            values["--to-revision"].ToLowerInvariant(), values["--install-root"], values["--data-root"], apply);
    }

    internal static void ValidateTarget(UnattendedUpgradeRequest request, ExistingInstallation? existing,
        string installedRevision, string packageRevision)
    {
        InstallerEngine.RequireExistingInstallation(existing, required: true);
        if (!SamePath(existing!.InstallRoot, request.InstallRoot) || !SamePath(existing.DataRoot, request.DataRoot))
            throw new InvalidOperationException("安装或数据目录与指定目标不一致，拒绝升级。");
        if (!string.Equals(request.FromRevision, installedRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("已安装版本发生变化，拒绝使用过期的升级请求。");
        if (!string.Equals(request.ToRevision, packageRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装包源码版本与指定目标不一致。");
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static UnattendedUpgradeResult Execute(UnattendedUpgradeRequest request, IProgress<string> progress) =>
        new InstallerEngine().UpgradeUnattended(request, progress);

    private sealed class TextProgress(TextWriter writer) : IProgress<string>
    {
        public void Report(string value) => writer.WriteLine(value);
    }
}
