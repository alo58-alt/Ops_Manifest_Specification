namespace CompanyOps.Agent.Deployment;

public static class ReleaseDirectoryMover
{
    // Newly extracted executables can briefly be held by Windows file scanners.
    // Retry only the same non-overwriting rename; never stop the process holding a file.
    private const int MaximumRetries = 50;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task MoveAsync(
        string source,
        string destination,
        CancellationToken cancellationToken,
        Action? onFirstRetry = null)
    {
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                retry < MaximumRetries && IsWindowsFileContention(exception) &&
                Directory.Exists(source) && !Directory.Exists(destination) && !File.Exists(destination) &&
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0)
            {
                if (retry == 0) onFirstRetry?.Invoke();
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsWindowsFileContention(Exception exception) =>
        OperatingSystem.IsWindows() && (exception is IOException or UnauthorizedAccessException) &&
        (exception.HResult & 0xffff) is 5 or 32 or 33;
}
