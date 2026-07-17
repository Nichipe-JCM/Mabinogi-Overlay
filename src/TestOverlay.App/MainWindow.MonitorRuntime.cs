using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private const int MonitorVisibilityRecoveryFrames = 2;

    private async void InternalTimerDebugTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsedCountdownSeconds = Math.Max(
            0,
            (int)Math.Floor((now - _lastInternalTimerCountdownAt).TotalSeconds));
        if (elapsedCountdownSeconds > 0)
        {
            _lastInternalTimerCountdownAt = _lastInternalTimerCountdownAt.AddSeconds(elapsedCountdownSeconds);
        }
        var reachedVerificationPoint = false;
        foreach (var timer in _internalBuffTimers.ToArray())
        {
            var previousSeconds = timer.RemainingSeconds;
            var minimumSeconds = MonitoredBuffCatalog.IsStatusBuff(timer.NameKey)
                ? 0
                : _monitorTestMode ? 0 : 1;
            timer.RemainingSeconds = Math.Max(
                minimumSeconds,
                timer.RemainingSeconds - elapsedCountdownSeconds);
            if (timer.RemainingSeconds == 0 && _statusObservations.ExpireBuffAtCountdownZero(timer.NameKey))
            {
                continue;
            }
            TryFireBuffAlert(timer, previousSeconds, timer.RemainingSeconds);
            reachedVerificationPoint |= previousSeconds > 30 && timer.RemainingSeconds <= 30;
        }

        _internalTimerOverlayWindow?.SetTimers(_internalBuffTimers);
        if (_monitorTestMode)
        {
            AdvanceMonitorTestTuairim();
            RefreshInternalTimerElementPreviews();
            return;
        }
        var needsVerification = _statusObservations.NeedsVerification;
        if (reachedVerificationPoint || now >= _nextMonitorValueRecognitionAt)
        {
            var reason = reachedVerificationPoint
                ? "threshold-30"
                : needsVerification ? "verification" : "periodic";
            await SynchronizeMonitorValuesAsync(reason);
        }
    }

    private void AdvanceMonitorTestTuairim()
    {
        var previousPercent = _statusObservations.TuairimPercent;
        var currentPercent = previousPercent;
        if (currentPercent >= 100)
        {
            _monitorTestTuairimFullSeconds++;
            if (_monitorTestTuairimFullSeconds >= TuairimFullEffectSeconds)
            {
                currentPercent = 0;
                _monitorTestTuairimFullSeconds = 0;
                _monitorTestTuairimChargeSeconds = 0;
                _tuairimAlertFired = false;
            }
        }
        else
        {
            _monitorTestTuairimChargeSeconds++;
            if (_monitorTestTuairimChargeSeconds >= TuairimNormalChargeSecondsPerPercent)
            {
                _monitorTestTuairimChargeSeconds = 0;
                currentPercent++;
                TryFireTuairimAlert(previousPercent, currentPercent, hadAcceptedObservation: true);
            }
        }

        if (currentPercent != previousPercent)
        {
            _statusObservations.SetTuairimPercentForTest(currentPercent);
            _internalTimerOverlayWindow?.SetTuairimPercent(currentPercent);
        }
    }

    private void RefreshInternalTimerOverlay()
    {
        if (!_overlayRuntime.IsRunning)
        {
            return;
        }

        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = null;
        if (!_buffMonitorEnabled && !_tuairimMonitorEnabled && _customTimerDefinitions.Count == 0)
        {
            _internalTimerDebugTimer.Stop();
            return;
        }

        StartInternalTimerOverlay();
    }

    private void StartInternalTimerOverlay()
    {
        var timerSlot = _buffMonitorEnabled && _selectedBuffNameKeys.Count > 0
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.InternalBuffTimer)
            : null;
        var tuairimSlot = _tuairimMonitorEnabled
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.TuairimGauge)
            : null;
        var alertSlot = (_buffMonitorEnabled ||
                         _tuairimMonitorEnabled ||
                         _customTimerDefinitions.Any(timer => timer.VisualAlertEnabled))
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.AlertNotification)
            : null;
        var customTimerSlot = _customTimerDefinitions.Count > 0
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.CustomTimer)
            : null;
        if (timerSlot is not null || tuairimSlot is not null || alertSlot is not null || customTimerSlot is not null)
        {
            _internalTimerOverlayWindow?.Close();
            _internalTimerOverlayWindow = new InternalTimerOverlayWindow(
                _layoutCanvasWidth,
                _layoutCanvasHeight,
                _overlayOpacity,
                timerSlot,
                tuairimSlot,
                alertSlot,
                customTimerSlot,
                _internalBuffTimers,
                _selectedBuffNameKeys)
            {
                Left = _overlayLeft,
                Top = _overlayTop
            };
            _internalTimerOverlayWindow.Show();
            _internalTimerOverlayWindow.UpdateLayout();
            _internalTimerOverlayWindow.SetTuairimPercent(_statusObservations.TuairimPercent);
            RefreshCustomTimerOverlay();
        }

        if (!_buffMonitorEnabled && !_tuairimMonitorEnabled)
        {
            _internalTimerDebugTimer.Stop();
            return;
        }
        _nextMonitorValueRecognitionAt = DateTimeOffset.MinValue;
        _lastInternalTimerCountdownAt = DateTimeOffset.UtcNow;
        _monitorFrameObscured = false;
        _monitorVisibleRecoveryFrames = 0;
        _monitorRecognitionRetryPolicy.Reset();
        _monitorValueRecognitionGeneration++;
        _internalTimerDebugTimer.Start();
        _ = SynchronizeMonitorValuesAsync("overlay-start");
    }

    private void RefreshInternalTimerElementPreviews()
    {
        SynchronizeMonitorElementDimensions();
        foreach (var slot in _overlaySlots.Where(slot => slot.Kind != OverlayElementKind.Quickslot))
        {
            slot.Preview = RenderMonitorElementPreview(slot.Kind);
        }
    }

    private async Task SynchronizeMonitorValuesAsync(string reason)
    {
        if (_monitorTestMode || _isMonitorValueRecognitionBusy || !_overlayRuntime.IsRunning)
        {
            return;
        }

        var shouldReadBuffs = _buffMonitorEnabled &&
                              _selectedBuffNameKeys.Count > 0 &&
                              _buffMonitorRoi is not null;
        var shouldReadTuairim = _tuairimMonitorEnabled && _tuairimAnchor is not null;
        if (!shouldReadBuffs && !shouldReadTuairim)
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }

        BitmapSource? frame;
        try
        {
            frame = CaptureMonitorFrame();
        }
        catch (Exception exception)
        {
            _log.Error("Monitor value capture failed.", exception);
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }

        if (frame is null)
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }
        var frameCapturedAt = DateTimeOffset.UtcNow;

        _isMonitorValueRecognitionBusy = true;
        var generation = _monitorValueRecognitionGeneration;
        try
        {
            var visibilityRoi = MonitorVisibilityRoi(shouldReadBuffs, shouldReadTuairim);
            if (visibilityRoi is Rect roi)
            {
                var visibility = MonitorFrameVisibilityEvaluator.Evaluate(frame, roi);
                if (visibility.IsObscured)
                {
                    MarkMonitorFrameObscured(
                        $"pixels:{visibility.Reason}, mean={visibility.MeanLuminance:0.0}, " +
                        $"deviation={visibility.LuminanceDeviation:0.0}, dark={visibility.DarkRatio:0.000}, " +
                        $"bright={visibility.BrightRatio:0.000}");
                    return;
                }

                if (!ConfirmMonitorFrameRecovery())
                {
                    return;
                }
            }

            if (shouldReadBuffs && _buffMonitorRoi is Rect buffRoi)
            {
                var previousMatches = _buffIconMatches.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var expectedAnchorCount = _buffIconMatches.Count;
                var evaluatedMatches = await Task.Run(() =>
                    _monitorTemplateDetection.EvaluateBuffAnchors(frame, _buffIconMatches.Values.ToArray()));
                if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                {
                    return;
                }
                var minimumVisibleAnchors = expectedAnchorCount >= 2
                    ? Math.Max(1, (int)Math.Ceiling(expectedAnchorCount * 0.4))
                    : 0;
                if (minimumVisibleAnchors > 0 && evaluatedMatches.Count < minimumVisibleAnchors)
                {
                    MarkMonitorFrameObscured($"anchors:{evaluatedMatches.Count}/{expectedAnchorCount}");
                    return;
                }
                foreach (var match in evaluatedMatches)
                {
                    _buffIconMatches[match.NameKey] = match;
                }

                var activeMatches = new List<BuffIconMatch>();
                foreach (var nameKey in _selectedBuffNameKeys.ToArray())
                {
                    var match = evaluatedMatches.FirstOrDefault(candidate => candidate.NameKey == nameKey);
                    var anchorState = BuffAnchorObservationClassifier.Classify(match);
                    if (!_statusObservations.ObserveBuffAnchorState(nameKey, anchorState, reason))
                    {
                        // Missing or low-confidence evidence does not change validation state.
                        // Confident inactivity is handled by the observation controller above.
                        continue;
                    }

                    var activeMatch = match!;

                    var transitionedFromOff = previousMatches.TryGetValue(nameKey, out var previousMatch) &&
                                              BuffAnchorObservationClassifier.Classify(previousMatch) ==
                                              BuffAnchorObservationState.Inactive &&
                                              _internalBuffTimers.All(timer => timer.NameKey != nameKey);
                    if (transitionedFromOff)
                    {
                        _pendingInitialBuffMinuteValidation.Add(nameKey);
                    }

                    activeMatches.Add(activeMatch);
                }

                var batchStartedAt = Stopwatch.GetTimestamp();
                var batch = await _monitorValueRecognition.ReadBuffTimesAsync(frame, buffRoi, activeMatches);
                var batchElapsed = Stopwatch.GetElapsedTime(batchStartedAt);
                if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                {
                    return;
                }

                _log.Info(
                    $"Buff OCR batch: reason={reason}, active={activeMatches.Count}, matched={batch.Reads.Count}, " +
                    $"elapsedMs={batchElapsed.TotalMilliseconds:0}, bounds={FormatRect(batch.Bounds)}, text={batch.RecognizedText}");
                var fallbackCount = 0;
                foreach (var activeMatch in activeMatches)
                {
                    var nameKey = activeMatch.NameKey;
                    var usedFallback = false;
                    if (!batch.Reads.TryGetValue(nameKey, out var read))
                    {
                        usedFallback = true;
                        fallbackCount++;
                        read = await _monitorValueRecognition.ReadBuffTimeAsync(frame, buffRoi, activeMatch.Bounds);
                        if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                        {
                            return;
                        }
                    }

                    if (read.RemainingSeconds is int remainingSeconds)
                    {
                        var compensatedSeconds = CompensateCapturedTimerValue(remainingSeconds, frameCapturedAt);
                        ApplyBuffTimeObservation(nameKey, compensatedSeconds, read.RecognizedText, reason);
                    }
                    else
                    {
                        SaveMonitorDiagnosticOnce(frame, read.Bounds, $"buff-{SanitizeDiagnosticName(nameKey)}");
                    }

                    _log.Info(
                        $"Buff OCR: reason={reason}, key={nameKey}, source={(usedFallback ? "row-fallback" : "batch")}, " +
                        $"stateConfidence={activeMatch.StateConfidence:0.000}, seconds={read.RemainingSeconds?.ToString() ?? "none"}, " +
                        $"bounds={FormatRect(read.Bounds)}, text={read.RecognizedText}");
                }
                _log.Info(
                    $"Buff OCR cycle complete: reason={reason}, active={activeMatches.Count}, " +
                    $"batchMatched={batch.Reads.Count}, fallbacks={fallbackCount}, " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(batchStartedAt).TotalMilliseconds:0}");
            }

            if (shouldReadTuairim && _tuairimAnchor is Rect tuairimAnchor)
            {
                var read = await _monitorValueRecognition.ReadTuairimPercentAsync(frame, tuairimAnchor);
                if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                {
                    return;
                }
                if (read.Percent is int percent)
                {
                    ApplyTuairimPercentObservation(percent, reason);
                }
                else
                {
                    SaveMonitorDiagnosticOnce(frame, read.Bounds, "tuairim-percent");
                }

                _log.Info(
                    $"Tuairim OCR: reason={reason}, percent={read.Percent?.ToString() ?? "none"}, " +
                    $"bounds={FormatRect(read.Bounds)}, text={read.RecognizedText}");
            }

            _internalTimerOverlayWindow?.SetTimers(_internalBuffTimers);
            RefreshInternalTimerElementPreviews();
        }
        catch (Exception exception)
        {
            _log.Error("Monitor value recognition failed.", exception);
        }
        finally
        {
            _isMonitorValueRecognitionBusy = false;
            if (generation == _monitorValueRecognitionGeneration)
            {
                var needsVerification = _statusObservations.NeedsVerification;
                var retryDelay = _monitorRecognitionRetryPolicy.CompleteAttempt(needsVerification);
                _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.Add(retryDelay);
            }
        }
    }

    private void ApplyTuairimPercentObservation(int observedPercent, string reason)
    {
        var result = _statusObservations.ObserveTuairimPercent(observedPercent, reason);
        if (!result.Accepted)
        {
            return;
        }

        _internalTimerOverlayWindow?.SetTuairimPercent(result.CurrentPercent);
        TryFireTuairimAlert(
            result.PreviousPercent,
            result.CurrentPercent,
            result.HadAcceptedObservation);
    }

    private void ResetTuairimPercentRecognitionState()
    {
        _statusObservations.ResetTuairim();
        _tuairimAlertFired = false;
    }

    private void ApplyBuffTimeObservation(string nameKey, int observedSeconds, string recognizedText, string reason)
    {
        var result = _statusObservations.ObserveBuffTime(
            nameKey,
            observedSeconds,
            recognizedText,
            reason,
            _alertAudio.Settings.BuffAlertSeconds);

        if (result.Accepted && result.Timer is not null && result.PreviousSeconds is int previousSeconds)
        {
            TryFireBuffAlert(result.Timer, previousSeconds, result.Timer.RemainingSeconds);
        }
    }
    private BitmapSource? CaptureMonitorFrame()
        => _captureSession.CaptureCurrentFrame(CurrentCaptureBackend);

    private static int CompensateCapturedTimerValue(int capturedSeconds, DateTimeOffset capturedAt)
    {
        if (capturedSeconds <= 0)
        {
            return 0;
        }

        var elapsedSeconds = Math.Max(
            0,
            (int)Math.Floor((DateTimeOffset.UtcNow - capturedAt).TotalSeconds));
        return Math.Max(1, capturedSeconds - elapsedSeconds);
    }

    private Rect? MonitorVisibilityRoi(bool shouldReadBuffs, bool shouldReadTuairim)
    {
        Rect? result = shouldReadBuffs ? _buffMonitorRoi : null;
        var tuairimRoi = shouldReadTuairim
            ? _tuairimMonitorRoi ?? _tuairimAnchor
            : null;
        if (tuairimRoi is not Rect next)
        {
            return result;
        }

        if (result is not Rect current)
        {
            return next;
        }

        current.Union(next);
        return current;
    }

    private void MarkMonitorFrameObscured(string detail)
    {
        _statusObservations.DiscardTransientVerificationEvidence();
        _monitorVisibleRecoveryFrames = 0;
        if (_monitorFrameObscured)
        {
            return;
        }

        _monitorFrameObscured = true;
        _log.Info($"Monitor frame obscured; preserving values and skipping OCR: {detail}");
    }

    private bool ConfirmMonitorFrameRecovery()
    {
        if (!_monitorFrameObscured)
        {
            return true;
        }

        _monitorVisibleRecoveryFrames++;
        if (_monitorVisibleRecoveryFrames < MonitorVisibilityRecoveryFrames)
        {
            return false;
        }

        _monitorFrameObscured = false;
        _monitorVisibleRecoveryFrames = 0;
        _log.Info("Monitor frame visibility recovered; OCR resumed after two visible frames.");
        return true;
    }

    private void SaveMonitorDiagnosticOnce(BitmapSource source, Rect bounds, string kind)
    {
        if (!_monitorDiagnosticKindsSaved.Add(kind))
        {
            return;
        }

        try
        {
            var prefix = $"monitor-{_log.SessionStartedAt:yyyyMMdd-HHmmss}-{kind}";
            MonitorValueRecognitionService.SaveDiagnosticImages(source, bounds, _log.LogDirectory, prefix);
            _log.Info($"Monitor OCR diagnostic saved: prefix={prefix}, bounds={FormatRect(bounds)}");
        }
        catch (Exception exception)
        {
            _log.Error($"Monitor OCR diagnostic save failed: kind={kind}", exception);
        }
    }

    private static string SanitizeDiagnosticName(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
}
