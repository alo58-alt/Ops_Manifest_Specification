using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CompanyOps.Agent.Deployment;
using Microsoft.Win32.SafeHandles;

namespace CompanyOps.Agent.Tests;

public sealed class ReleaseDirectoryMoverTests
{
    [Fact]
    public async Task RealWindowsDirectoryLock_IsRetriedUntilReleased()
    {
        using var directory = new TestDirectory();
        var (source, destination) = CreatePaths(directory.FullPath);
        using var handle = HoldDirectory(source);
        var originalFailure = Assert.ThrowsAny<IOException>(() => Directory.Move(source, destination));
        Assert.Contains(originalFailure.HResult & 0xffff, new[] { 5, 32, 33 });
        var retryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = ReleaseDirectoryMover.MoveAsync(source, destination,
            TestContext.Current.CancellationToken, () => retryObserved.TrySetResult());
        await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
        handle.Dispose();
        await move;
        Assert.False(Directory.Exists(source));
        Assert.Equal("inert payload", await File.ReadAllTextAsync(Path.Combine(destination, "payload.txt"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistentWindowsDirectoryLock_FailsWithinBound_AndPreservesSource()
    {
        using var directory = new TestDirectory();
        var (source, destination) = CreatePaths(directory.FullPath);
        using var handle = HoldDirectory(source);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<IOException>(() => ReleaseDirectoryMover.MoveAsync(
            source, destination, TestContext.Current.CancellationToken));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "A persistent lock must not wait indefinitely.");
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("inert payload", File.ReadAllText(Path.Combine(source, "payload.txt")));
    }

    [Fact]
    public async Task CancellationDuringLockWait_StopsWithoutMoving()
    {
        using var directory = new TestDirectory();
        var (source, destination) = CreatePaths(directory.FullPath);
        using var handle = HoldDirectory(source);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var retryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = ReleaseDirectoryMover.MoveAsync(source, destination, cancellation.Token,
            () => retryObserved.TrySetResult());
        await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExistingDestination_IsNotOverwrittenOrRetried()
    {
        using var directory = new TestDirectory();
        var (source, destination) = CreatePaths(directory.FullPath);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "previous release");
        var retried = false;
        await Assert.ThrowsAnyAsync<IOException>(() => ReleaseDirectoryMover.MoveAsync(source, destination,
            TestContext.Current.CancellationToken, () => retried = true));
        Assert.False(retried);
        Assert.True(Directory.Exists(source));
        Assert.Equal("previous release", File.ReadAllText(Path.Combine(destination, "existing.txt")));
    }

    private static (string Source, string Destination) CreatePaths(string root)
    {
        var source = Path.Combine(root, "staging");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), "inert payload");
        return (source, Path.Combine(root, "release"));
    }

    private static SafeFileHandle HoldDirectory(string path)
    {
        // Deny FILE_SHARE_DELETE on this test-owned directory, reproducing a real rename conflict.
        var handle = CreateFile(path, 0x80000000, 1 | 2, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
