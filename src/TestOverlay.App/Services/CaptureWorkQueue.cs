using System.Diagnostics;

namespace TestOverlay.App.Services;

// Owns desktop capture serialization and invalidates queued/in-flight work on stop.
internal sealed class CaptureWorkQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _generation;

    public async Task<T?> RunAsync<T>(Func<T> capture, Action<double, long>? measured = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var generation = Volatile.Read(ref _generation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _generation)) return null;
            var value = await Task.Run(() =>
            {
                var start = Stopwatch.GetTimestamp();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                try { return capture(); }
                finally { measured?.Invoke(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - allocated); }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return generation == Volatile.Read(ref _generation) ? value : null;
        }
        catch (Exception) when (generation != Volatile.Read(ref _generation)) { return null; }
        finally { _gate.Release(); }
    }

    public async Task ResetAsync(Action cleanup)
    {
        Interlocked.Increment(ref _generation);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(cleanup).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
