using CompanyOps.Contracts;

namespace CompanyOps.Agent.Onboarding;

public sealed class ProjectDirectoryBrowser
{
    private const int MaximumDirectories = 500;

    public DirectoryBrowseResult Browse(DirectoryBrowseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(drive => new DirectoryBrowseEntry(drive.Name, drive.RootDirectory.FullName))
                .ToArray();
            return new DirectoryBrowseResult(null, null, false, drives);
        }

        var currentPath = ResolveLocalDirectory(request.Path);
        var current = new DirectoryInfo(currentPath);
        var directories = new List<DirectoryBrowseEntry>();
        try
        {
            foreach (var directory in current.EnumerateDirectories()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    directories.Add(new DirectoryBrowseEntry(directory.Name, directory.FullName));
                    if (directories.Count >= MaximumDirectories)
                    {
                        break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A directory may disappear or become inaccessible during enumeration.
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidDataException($"没有权限浏览服务器目录：{currentPath}");
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"无法浏览服务器目录 {currentPath}：{exception.Message}");
        }

        var root = Path.GetPathRoot(currentPath)!;
        var parentPath = string.Equals(
                Path.TrimEndingDirectorySeparator(currentPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase)
            ? null
            : current.Parent?.FullName;
        var isProjectRoot = File.Exists(Path.Combine(currentPath, "ops", "project-manifest.json"));
        return new DirectoryBrowseResult(currentPath, parentPath, isProjectRoot, directories);
    }

    private static string ResolveLocalDirectory(string input)
    {
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim())));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"服务器目录无效：{exception.Message}");
        }

        var root = Path.GetPathRoot(fullPath);
        if (!Path.IsPathFullyQualified(fullPath) || root is null ||
            !root.EndsWith(@":\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("目录选择器只允许浏览服务器本机磁盘。");
        }
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidDataException($"服务器目录不存在：{fullPath}");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("目录选择器不允许进入重解析点。");
        }
        return fullPath;
    }
}
