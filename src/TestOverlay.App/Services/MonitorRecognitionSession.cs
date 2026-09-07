namespace TestOverlay.App.Services;

// Owned by the UI thread. An attempt retains its cancellation source until all
// asynchronous recognition work has returned, even when the window closes.
internal sealed class MonitorRecognitionSession : IDisposable
{
    private Attempt? _active;
    private int _generation;
    private bool _disposed;
    public bool IsBusy => _active is not null;

    public Attempt? TryBegin()
    {
        if (_disposed || IsBusy) return null;
        return _active = new Attempt(this, _generation);
    }

    public void Reset()
    {
        _generation++;
        _active?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    internal sealed class Attempt : IDisposable
    {
        private readonly MonitorRecognitionSession _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _completed;
        internal Attempt(MonitorRecognitionSession owner, int generation)
        {
            _owner = owner;
            Generation = generation;
            Token = _cancellation.Token;
        }
        public int Generation { get; }
        public CancellationToken Token { get; }
        public bool IsCurrent => !_completed && !_owner._disposed && Generation == _owner._generation;
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose()
        {
            if (_completed) return;
            _completed = true;
            if (ReferenceEquals(_owner._active, this)) _owner._active = null;
            _cancellation.Dispose();
        }
    }
}
