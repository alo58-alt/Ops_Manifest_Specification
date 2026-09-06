namespace CompanyOps.Setup;

internal sealed class PlatformSetupLease : IDisposable
{
    private readonly Mutex _mutex;

    private PlatformSetupLease(Mutex mutex) => _mutex = mutex;

    internal static PlatformSetupLease Acquire(string name = @"Global\CompanyOps.PlatformSetup.v1")
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException("另一项 CompanyOps 安装或升级正在执行，请等待原窗口完成。");
            return new PlatformSetupLease(mutex);
        }
        catch (AbandonedMutexException)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw new InvalidOperationException("上一次 CompanyOps 安装异常退出，请先核对服务与目录状态后再升级。");
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
