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

    public bool HasTuanExtension { get; set; }

    public bool HasHarmony { get; set; }

    public string LastRecognizedText { get; set; } = string.Empty;

    public int ConsecutiveZeroConfirmations { get; set; }

    public int? PendingObservedSeconds { get; set; }

    public int PendingObservationConfirmations { get; set; }

    public bool NeedsFastVerification =>
        ConsecutiveZeroConfirmations > 0 || PendingObservedSeconds is not null || RemainingSeconds <= 1;
}
