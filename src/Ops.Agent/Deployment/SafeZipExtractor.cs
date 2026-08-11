using System.IO.Compression;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Deployment;

public sealed class SafeZipExtractor
{
    private const int MaximumEntries = 20_000;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    public async Task ExtractAsync(
        ValidatedArtifact artifact,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);
        var prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(artifact.FullPath);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("ZIP 条目数量超过限制");
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsUnixSymlink(entry) || Path.IsPathRooted(entry.FullName))
            {
                throw new InvalidDataException($"ZIP 含不允许的链接或绝对路径：{entry.FullName}");
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"ZIP 路径逃逸：{entry.FullName}");
            }

            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("ZIP 解压后大小超过限制");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static bool IsUnixSymlink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
