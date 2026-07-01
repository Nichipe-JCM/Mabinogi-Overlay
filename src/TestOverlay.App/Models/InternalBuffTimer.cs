namespace TestOverlay.App.Models;

public sealed class InternalBuffTimer
{
    public InternalBuffTimer(string nameKey, int remainingSeconds)
    {
        NameKey = nameKey;
        RemainingSeconds = Math.Max(0, remainingSeconds);
    }

    public string NameKey { get; }

    public int RemainingSeconds { get; set; }
}
