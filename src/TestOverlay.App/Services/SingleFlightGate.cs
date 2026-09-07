namespace TestOverlay.App.Services;

internal sealed class SingleFlightGate
{
    private int _entered;

    public bool TryEnter() => Interlocked.CompareExchange(ref _entered, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _entered, 0);
}
