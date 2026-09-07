namespace TestOverlay.App.Services;

/// <summary>
/// Schedules monitor recognition retries independently from value validation.
/// Validation remains conservative while repeated inconclusive attempts gradually back off.
/// </summary>
public sealed class MonitorRecognitionRetryPolicy
{
    private const int ImmediateRetryAttempts = 6;
    private const int ModerateRetryAttempts = 12;
    private static readonly TimeSpan NormalDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ModerateDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowDelay = TimeSpan.FromSeconds(3);

    private int _consecutiveVerificationAttempts;

    public int ConsecutiveVerificationAttempts => _consecutiveVerificationAttempts;

    public TimeSpan CompleteAttempt(bool needsVerification)
    {
        if (!needsVerification)
        {
            Reset();
            return NormalDelay;
        }

        _consecutiveVerificationAttempts++;
        if (_consecutiveVerificationAttempts <= ImmediateRetryAttempts)
        {
            return TimeSpan.Zero;
        }

        return _consecutiveVerificationAttempts <= ModerateRetryAttempts
            ? ModerateDelay
            : SlowDelay;
    }

    public void Reset() => _consecutiveVerificationAttempts = 0;
}
