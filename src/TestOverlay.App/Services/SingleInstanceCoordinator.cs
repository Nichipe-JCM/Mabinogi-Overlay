using System.Threading;
using System.Windows;

namespace TestOverlay.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _mutexName;
    private readonly EventWaitHandle _activation;
    private Mutex? _mutex;
    private RegisteredWaitHandle? _listener;
    private bool _ownsMutex;
    private int _disposed;

    public SingleInstanceCoordinator() : this(TestLabEnvironment.InstanceName) { }

    internal SingleInstanceCoordinator(string name)
    {
        _mutexName = name;
        // Keep an early activation request pending until the primary window is ready.
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
    }

    public bool TryAcquirePrimary()
    {
        if (_ownsMutex) return true;
        _mutex ??= new Mutex(false, _mutexName);
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    public void SignalPrimaryInstance() => _activation.Set();

    public void Attach(Window window, Action activate)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(activate);
        Listen(() =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0) activate();
                });
        });
    }

    internal void Listen(Action activate)
    {
        _listener?.Unregister(null);
        _listener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0) activate();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener?.Unregister(null);
        _listener = null;
        _activation.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
