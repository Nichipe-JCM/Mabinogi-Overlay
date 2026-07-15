using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class StatusObservationController
{
    private const int RecognitionIntervalSeconds = 2;
    private const int StatusBuffConfirmationSeconds = 3;
    private const int StatusBuffMinimumConfirmations = 3;
    private readonly AppLog _log;
    private bool _hasTuairimObservation;
    private int? _pendingTuairimPercent;
    private int _pendingTuairimConfirmations;
    private DateTimeOffset? _lastAcceptedTuairimAt;

    public StatusObservationController(AppLog log) => _log = log;

    public List<InternalBuffTimer> Timers { get; } = [];
    public HashSet<string> PendingInitialBuffValidation { get; } = new(StringComparer.Ordinal);
    public int TuairimPercent { get; private set; }
    public bool NeedsVerification =>
        PendingInitialBuffValidation.Count > 0 || Timers.Any(timer => timer.NeedsVerification);

    public bool ObserveBuffAnchorState(
        string nameKey,
        BuffAnchorObservationState state,
        string reason,
        DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        switch (state)
        {
            case BuffAnchorObservationState.Active:
                ResetBuffZeroConfirmation(nameKey);
                return true;
            case BuffAnchorObservationState.Inactive:
                PendingInitialBuffValidation.Remove(nameKey);
                RegisterBuffZeroConfirmation(nameKey, reason, "inactive", now);
                return false;
            default:
                return false;
        }
    }

    public TuairimObservationResult ObserveTuairimPercent(
        int observedPercent,
        string reason,
        DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        observedPercent = Math.Clamp(observedPercent, 0, 100);
        var hadAcceptedObservation = _hasTuairimObservation;
        var previousPercent = TuairimPercent;
        if (!_hasTuairimObservation)
        {
            if (!ConfirmInitialTuairimPercent(observedPercent, reason))
            {
                return TuairimObservationResult.Pending(previousPercent);
            }
        }
        else if (observedPercent == 0 && TuairimPercent != 0)
        {
            _pendingTuairimConfirmations = _pendingTuairimPercent == 0
                ? _pendingTuairimConfirmations + 1
                : 1;
            _pendingTuairimPercent = 0;
            _log.Info($"Tuairim zero validating: reason={reason}, count={_pendingTuairimConfirmations}/2");
            if (_pendingTuairimConfirmations < 2)
            {
                return TuairimObservationResult.Pending(previousPercent);
            }
        }
        else
        {
            var elapsedSeconds = Math.Max(
                RecognitionIntervalSeconds,
                (now - (_lastAcceptedTuairimAt ?? now)).TotalSeconds);
            var elapsedIntervals = Math.Max(
                1,
                (int)Math.Floor((elapsedSeconds + 0.25) / RecognitionIntervalSeconds));
            var allowedIncrease = Math.Max(5, elapsedIntervals * 5);
            if (observedPercent < TuairimPercent || observedPercent > TuairimPercent + allowedIncrease)
            {
                _log.Info(
                    $"Tuairim OCR rejected implausible change: reason={reason}, current={TuairimPercent}, " +
                    $"observed={observedPercent}, allowedIncrease={allowedIncrease}");
                return TuairimObservationResult.RejectedValue(previousPercent);
            }
        }

        ClearPendingTuairimPercent();
        _hasTuairimObservation = true;
        TuairimPercent = observedPercent;
        _lastAcceptedTuairimAt = now;
        return TuairimObservationResult.AcceptedValue(
            previousPercent,
            observedPercent,
            hadAcceptedObservation);
    }

    public BuffObservationResult ObserveBuffTime(
        string nameKey,
        int observedSeconds,
        string recognizedText,
        string reason,
        int alertThreshold,
        DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var timer = Timers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        var previousSeconds = timer?.RemainingSeconds;
        var isMusicBuff = MonitoredBuffCatalog.IsMusicBuff(nameKey);
        var recognizedTuan = isMusicBuff &&
            (recognizedText.Contains("\uD22C\uC548", StringComparison.Ordinal) ||
             recognizedText.Contains("\uC758 \uB178\uB798", StringComparison.Ordinal) ||
             recognizedText.Contains("\uC758\uB178\uB798", StringComparison.Ordinal));
        if (observedSeconds <= 0)
        {
            if (MonitoredBuffCatalog.IsStatusBuff(nameKey))
            {
                ExpireBuffImmediately(nameKey, reason, "ocr-zero");
            }
            else
            {
                RegisterBuffZeroConfirmation(nameKey, reason, "ocr-zero", now);
            }
            return BuffObservationResult.Unchanged(timer, previousSeconds);
        }

        if (!MonitoredBuffCatalog.IsStatusBuff(nameKey) &&
            timer is null &&
            PendingInitialBuffValidation.Contains(nameKey) &&
            observedSeconds < 60)
        {
            _log.Info(
                $"Buff OCR rejected short initial activation: reason={reason}, key={nameKey}, observed={observedSeconds}");
            return BuffObservationResult.RejectedValue;
        }

        PendingInitialBuffValidation.Remove(nameKey);
        if (timer is null)
        {
            timer = new InternalBuffTimer(nameKey, observedSeconds)
            {
                AlertFired = observedSeconds <= alertThreshold
            };
            Timers.Add(timer);
        }
        else
        {
            ApplyExistingBuffObservation(timer, observedSeconds, recognizedTuan, reason, now);
        }

        timer.LastRecognizedText = recognizedText;
        timer.HasTuanExtension = isMusicBuff && (timer.HasTuanExtension || recognizedTuan);
        timer.HasHarmony = isMusicBuff && recognizedText.Contains("\uD558\uBAA8\uB2C8", StringComparison.Ordinal);
        return new BuffObservationResult(true, timer, previousSeconds, timer.RemainingSeconds);
    }

    public bool ExpireBuffAtCountdownZero(string nameKey)
    {
        if (!MonitoredBuffCatalog.IsStatusBuff(nameKey))
        {
            return false;
        }

        return ExpireBuffImmediately(nameKey, "countdown", "timer-zero");
    }

    private void RegisterBuffZeroConfirmation(
        string nameKey,
        string reason,
        string source,
        DateTimeOffset now)
    {
        var timer = Timers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is null)
        {
            return;
        }

        timer.RemainingSeconds = Math.Max(1, timer.RemainingSeconds);
        ClearPendingTimeObservation(timer);
        timer.ZeroConfirmationStartedAt ??= now;
        timer.ConsecutiveZeroConfirmations++;
        var validFor = now - timer.ZeroConfirmationStartedAt.Value;
        var isStatusBuff = MonitoredBuffCatalog.IsStatusBuff(nameKey);
        var requiredConfirmations = isStatusBuff ? StatusBuffMinimumConfirmations : 5;
        _log.Info(
            $"Buff zero confirmation: reason={reason}, key={nameKey}, source={source}, " +
            $"count={timer.ConsecutiveZeroConfirmations}/{requiredConfirmations}, validMs={validFor.TotalMilliseconds:0}");
        if (timer.ConsecutiveZeroConfirmations >= requiredConfirmations &&
            (!isStatusBuff || validFor >= TimeSpan.FromSeconds(StatusBuffConfirmationSeconds)))
        {
            ExpireBuffImmediately(nameKey, reason, source);
        }
    }

    private bool ExpireBuffImmediately(string nameKey, string reason, string source)
    {
        var timer = Timers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is null)
        {
            return false;
        }

        Timers.Remove(timer);
        PendingInitialBuffValidation.Remove(nameKey);
        _log.Info($"Buff expired: reason={reason}, key={nameKey}, source={source}");
        return true;
    }

    private void ResetBuffZeroConfirmation(string nameKey)
    {
        var timer = Timers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is not null)
        {
            timer.ConsecutiveZeroConfirmations = 0;
            timer.ZeroConfirmationStartedAt = null;
        }
    }

    public void ResetTuairim()
    {
        _hasTuairimObservation = false;
        _lastAcceptedTuairimAt = null;
        ClearPendingTuairimPercent();
    }

    public void SetTuairimPercentForTest(int percent, DateTimeOffset? observedAt = null)
    {
        TuairimPercent = Math.Clamp(percent, 0, 100);
        _hasTuairimObservation = true;
        _lastAcceptedTuairimAt = observedAt ?? DateTimeOffset.UtcNow;
        ClearPendingTuairimPercent();
    }

    private bool ConfirmInitialTuairimPercent(int observedPercent, string reason)
    {
        if (_pendingTuairimPercent is int pending &&
            observedPercent >= pending &&
            observedPercent <= pending + 5)
        {
            _pendingTuairimConfirmations++;
        }
        else
        {
            _pendingTuairimPercent = observedPercent;
            _pendingTuairimConfirmations = 1;
        }

        _log.Info(
            $"Tuairim initial validating: reason={reason}, observed={observedPercent}, " +
            $"count={_pendingTuairimConfirmations}/2");
        return _pendingTuairimConfirmations >= 2;
    }

    private void ApplyExistingBuffObservation(
        InternalBuffTimer timer,
        int observedSeconds,
        bool recognizedTuan,
        string reason,
        DateTimeOffset now)
    {
        timer.ConsecutiveZeroConfirmations = 0;
        timer.ZeroConfirmationStartedAt = null;
        if (MonitoredBuffCatalog.IsStatusBuff(timer.NameKey))
        {
            ApplyStatusBuffObservation(timer, observedSeconds, reason, now);
            return;
        }

        if (recognizedTuan && !timer.HasTuanExtension && timer.RemainingSeconds <= 5)
        {
            timer.AwaitingTuanExtensionRefresh = true;
        }

        timer.HasTuanExtension |= recognizedTuan;
        if (timer.AwaitingTuanExtensionRefresh && timer.RemainingSeconds <= 5 && observedSeconds > 30)
        {
            timer.RemainingSeconds = observedSeconds;
            timer.AwaitingTuanExtensionRefresh = false;
            ClearPendingTimeObservation(timer);
            _log.Info(
                $"Buff OCR applied Tuan extension immediately: reason={reason}, " +
                $"key={timer.NameKey}, observed={observedSeconds}");
            return;
        }

        var difference = observedSeconds - timer.RemainingSeconds;
        if (difference < -3)
        {
            ApplyDownwardObservation(timer, observedSeconds, reason, now);
        }
        else if (difference <= 12)
        {
            timer.RemainingSeconds = observedSeconds;
            ClearPendingTimeObservation(timer);
        }
        else if (!timer.PendingObservationIsDownward &&
                 timer.PendingObservedSeconds is int pending &&
                 Math.Abs(pending - observedSeconds) <= 4)
        {
            timer.PendingObservationConfirmations++;
            if (timer.PendingObservationConfirmations >= 2)
            {
                timer.RemainingSeconds = observedSeconds;
                ClearPendingTimeObservation(timer);
            }
        }
        else
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations = 1;
            timer.PendingObservationStartedAt = now;
            timer.PendingObservationIsDownward = false;
            _log.Info(
                $"Buff OCR deferred: reason={reason}, key={timer.NameKey}, " +
                $"current={timer.RemainingSeconds}, observed={observedSeconds}");
        }
    }

    private void ApplyStatusBuffObservation(
        InternalBuffTimer timer,
        int observedSeconds,
        string reason,
        DateTimeOffset now)
    {
        var difference = observedSeconds - timer.RemainingSeconds;
        if (difference < -3)
        {
            ApplyStatusBuffDownwardObservation(timer, observedSeconds, reason, now);
            return;
        }

        if (difference <= 12)
        {
            timer.RemainingSeconds = observedSeconds;
            ClearPendingTimeObservation(timer);
            return;
        }

        var continues = !timer.PendingObservationIsDownward &&
                        timer.PendingObservedSeconds is int pending &&
                        observedSeconds <= pending + 2 &&
                        observedSeconds >= pending - 4;
        if (continues)
        {
            timer.PendingObservationConfirmations++;
        }
        else
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations = 1;
            timer.PendingObservationStartedAt = now;
            timer.PendingObservationIsDownward = false;
        }

        var validFor = now - (timer.PendingObservationStartedAt ?? now);
        if (validFor >= TimeSpan.FromSeconds(StatusBuffConfirmationSeconds) &&
            timer.PendingObservationConfirmations >= StatusBuffMinimumConfirmations)
        {
            timer.RemainingSeconds = observedSeconds;
            ClearPendingTimeObservation(timer);
            _log.Info(
                $"Status buff refresh accepted: reason={reason}, key={timer.NameKey}, " +
                $"observed={observedSeconds}, validMs={validFor.TotalMilliseconds:0}");
            return;
        }

        _log.Info(
            $"Status buff refresh validating: reason={reason}, key={timer.NameKey}, " +
            $"current={timer.RemainingSeconds}, observed={observedSeconds}, " +
            $"count={timer.PendingObservationConfirmations}, validMs={validFor.TotalMilliseconds:0}");
    }

    private void ApplyStatusBuffDownwardObservation(
        InternalBuffTimer timer,
        int observedSeconds,
        string reason,
        DateTimeOffset now)
    {
        var continues = timer.PendingObservationIsDownward &&
                        timer.PendingObservedSeconds is int pending &&
                        observedSeconds <= pending + 1 &&
                        observedSeconds >= pending - 4;
        if (continues)
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations++;
        }
        else
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations = 1;
            timer.PendingObservationStartedAt = now;
            timer.PendingObservationIsDownward = true;
        }

        var validFor = now - (timer.PendingObservationStartedAt ?? now);
        if (validFor >= TimeSpan.FromSeconds(StatusBuffConfirmationSeconds) &&
            timer.PendingObservationConfirmations >= StatusBuffMinimumConfirmations)
        {
            timer.RemainingSeconds = observedSeconds;
            ClearPendingTimeObservation(timer);
            _log.Info(
                $"Status buff OCR resynchronized sustained downward value: reason={reason}, key={timer.NameKey}, " +
                $"observed={observedSeconds}, validMs={validFor.TotalMilliseconds:0}");
            return;
        }

        _log.Info(
            $"Status buff OCR validating downward value: reason={reason}, key={timer.NameKey}, " +
            $"current={timer.RemainingSeconds}, observed={observedSeconds}, " +
            $"count={timer.PendingObservationConfirmations}, validMs={validFor.TotalMilliseconds:0}");
    }

    private void ApplyDownwardObservation(
        InternalBuffTimer timer,
        int observedSeconds,
        string reason,
        DateTimeOffset now)
    {
        var continues = timer.PendingObservationIsDownward &&
                        timer.PendingObservedSeconds is int pending &&
                        observedSeconds <= pending + 1 &&
                        observedSeconds >= pending - 4;
        if (continues)
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations++;
        }
        else
        {
            timer.PendingObservedSeconds = observedSeconds;
            timer.PendingObservationConfirmations = 1;
            timer.PendingObservationStartedAt = now;
            timer.PendingObservationIsDownward = true;
        }

        var validFor = now - (timer.PendingObservationStartedAt ?? now);
        if (validFor >= TimeSpan.FromSeconds(5) && timer.PendingObservationConfirmations >= 5)
        {
            timer.RemainingSeconds = observedSeconds;
            ClearPendingTimeObservation(timer);
            _log.Info(
                $"Buff OCR accepted sustained downward value: reason={reason}, key={timer.NameKey}, " +
                $"observed={observedSeconds}, validMs={validFor.TotalMilliseconds:0}");
        }
        else
        {
            _log.Info(
                $"Buff OCR validating downward value: reason={reason}, key={timer.NameKey}, " +
                $"current={timer.RemainingSeconds}, observed={observedSeconds}, " +
                $"count={timer.PendingObservationConfirmations}, validMs={validFor.TotalMilliseconds:0}");
        }
    }

    private void ClearPendingTuairimPercent()
    {
        _pendingTuairimPercent = null;
        _pendingTuairimConfirmations = 0;
    }

    private static void ClearPendingTimeObservation(InternalBuffTimer timer)
    {
        timer.PendingObservedSeconds = null;
        timer.PendingObservationConfirmations = 0;
        timer.PendingObservationStartedAt = null;
        timer.PendingObservationIsDownward = false;
    }
}

public sealed record TuairimObservationResult(
    bool Accepted,
    bool Rejected,
    int PreviousPercent,
    int CurrentPercent,
    bool HadAcceptedObservation)
{
    public static TuairimObservationResult AcceptedValue(int previous, int current, bool hadAccepted) =>
        new(true, false, previous, current, hadAccepted);
    public static TuairimObservationResult Pending(int current) =>
        new(false, false, current, current, false);
    public static TuairimObservationResult RejectedValue(int current) =>
        new(false, true, current, current, true);
}

public sealed record BuffObservationResult(
    bool Accepted,
    InternalBuffTimer? Timer,
    int? PreviousSeconds,
    int? CurrentSeconds)
{
    public static BuffObservationResult RejectedValue { get; } = new(false, null, null, null);
    public static BuffObservationResult Unchanged(InternalBuffTimer? timer, int? previous) =>
        new(false, timer, previous, timer?.RemainingSeconds);
}
