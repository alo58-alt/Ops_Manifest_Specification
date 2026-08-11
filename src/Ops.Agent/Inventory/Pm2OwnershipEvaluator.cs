namespace CompanyOps.Agent.Inventory;

public static class Pm2OwnershipEvaluator
{
    public static Pm2OwnershipResult Evaluate(
        LegacyPm2Claim claim,
        Pm2SnapshotReadResult snapshotResult)
    {
        if (snapshotResult.State != Pm2OwnershipState.Matched ||
            snapshotResult.Snapshot is null)
        {
            return new Pm2OwnershipResult(
                snapshotResult.State,
                snapshotResult.Detail);
        }

        var matches = snapshotResult.Snapshot.Processes
            .Where(
                process =>
                    string.Equals(
                        process.Name,
                        claim.ProcessName,
                        StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return new Pm2OwnershipResult(
                Pm2OwnershipState.Missing,
                $"PM2 中不存在精确名称 {claim.ProcessName}");
        }

        if (matches.Length > 1)
        {
            return new Pm2OwnershipResult(
                Pm2OwnershipState.Conflict,
                $"PM2 中存在 {matches.Length} 个同名进程 {claim.ProcessName}");
        }

        var process = matches[0];
        if (!PathEquals(process.Cwd, claim.ExpectedCwd) ||
            !PathEquals(process.Script, claim.ExpectedScript))
        {
            return new Pm2OwnershipResult(
                Pm2OwnershipState.Conflict,
                "PM2 名称命中，但 cwd 或 script 与声明不一致",
                process);
        }

        return new Pm2OwnershipResult(
            Pm2OwnershipState.Matched,
            "PM2 name/cwd/script 唯一精确匹配",
            process);
    }

    private static bool PathEquals(string actual, string? expected)
    {
        if (expected is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizePath(actual),
                NormalizePath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
