using System.Collections.Concurrent;

namespace CompanyOps.Agent.Operations;

public sealed class OperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    public OperationGateLease? TryAcquire(IEnumerable<string> resourceKeys)
    {
        var keys = resourceKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var acquired = new List<SemaphoreSlim>();
        foreach (var key in keys)
        {
            var semaphore = _resources.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            if (!semaphore.Wait(0))
            {
                foreach (var held in acquired)
                {
                    held.Release();
                }

                return null;
            }

            acquired.Add(semaphore);
        }

        return new OperationGateLease(acquired);
    }
}

public sealed class OperationGateLease(IReadOnlyList<SemaphoreSlim> semaphores) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var semaphore in semaphores)
        {
            semaphore.Release();
        }
    }
}
