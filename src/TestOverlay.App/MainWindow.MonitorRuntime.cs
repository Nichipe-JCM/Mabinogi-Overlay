using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private async void InternalTimerDebugTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var reachedVerificationPoint = false;
        foreach (var timer in _internalBuffTimers)
        {
            var previousSeconds = timer.RemainingSeconds;
            timer.RemainingSeconds = Math.Max(_monitorTestMode ? 0 : 1, timer.RemainingSeconds - 1);
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
        var needsFastVerification = _statusObservations.NeedsFastRetry;
        if (reachedVerificationPoint || needsFastVerification || now >= _nextMonitorValueRecognitionAt)
        {
            var reason = reachedVerificationPoint
                ? "threshold-30"
                : needsFastVerification ? "fast-verification" : "periodic";
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
        if (!_buffMonitorEnabled && !_tuairimMonitorEnabled)
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
        var alertSlot = (_buffMonitorEnabled || _tuairimMonitorEnabled)
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.AlertNotification)
            : null;
        if (timerSlot is null && tuairimSlot is null && alertSlot is null)
        {
            return;
        }

        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = new InternalTimerOverlayWindow(
            _layoutCanvasWidth,
            _layoutCanvasHeight,
            _overlayOpacity,
            timerSlot,
            tuairimSlot,
            alertSlot,
            _internalBuffTimers,
            _selectedBuffNameKeys)
        {
            Left = _overlayLeft,
            Top = _overlayTop
        };
        _internalTimerOverlayWindow.Show();
        _internalTimerOverlayWindow.UpdateLayout();
        _internalTimerOverlayWindow.SetTuairimPercent(_statusObservations.TuairimPercent);
        _nextMonitorValueRecognitionAt = DateTimeOffset.MinValue;
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

        _isMonitorValueRecognitionBusy = true;
        var generation = _monitorValueRecognitionGeneration;
        try
        {
            if (shouldReadBuffs && _buffMonitorRoi is Rect buffRoi)
            {
                var previousMatches = _buffIconMatches.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var evaluatedMatches = await Task.Run(() =>
                    _monitorTemplateDetection.EvaluateBuffAnchors(frame, _buffIconMatches.Values.ToArray()));
                if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                {
                    return;
                }
                foreach (var match in evaluatedMatches)
                {
                    _buffIconMatches[match.NameKey] = match;
                }

                foreach (var nameKey in _selectedBuffNameKeys.ToArray())
                {
                    var match = evaluatedMatches.FirstOrDefault(candidate => candidate.NameKey == nameKey);
                    if (match is null)
                    {
                        _statusObservations.ResetPendingTimeObservation(nameKey);
                        _statusObservations.ResetBuffZeroConfirmation(nameKey);
                        continue;
                    }

                    if (!match.IsActive && match.StateConfidence >= 0.03)
                    {
                        _pendingInitialBuffMinuteValidation.Remove(nameKey);
                        _statusObservations.RegisterBuffZeroConfirmation(nameKey, reason, "inactive");
                        continue;
                    }
                    if (!match.IsActive)
                    {
                        _pendingInitialBuffMinuteValidation.Remove(nameKey);
                        _statusObservations.ResetBuffZeroConfirmation(nameKey);
                        continue;
                    }

                    var transitionedFromOff = previousMatches.TryGetValue(nameKey, out var previousMatch) &&
                                              !previousMatch.IsActive &&
                                              _internalBuffTimers.All(timer => timer.NameKey != nameKey);
                    if (transitionedFromOff)
                    {
                        _pendingInitialBuffMinuteValidation.Add(nameKey);
                    }

                    var read = await _monitorValueRecognition.ReadBuffTimeAsync(frame, buffRoi, match.Bounds);
                    if (generation != _monitorValueRecognitionGeneration || !_overlayRuntime.IsRunning)
                    {
                        return;
                    }
                    if (read.RemainingSeconds is int remainingSeconds)
                    {
                        ApplyBuffTimeObservation(nameKey, remainingSeconds, read.RecognizedText, reason);
                    }
                    else
                    {
                        _statusObservations.ResetPendingTimeObservation(nameKey);
                        _statusObservations.ResetBuffZeroConfirmation(nameKey);
                        SaveMonitorDiagnosticOnce(frame, read.Bounds, $"buff-{SanitizeDiagnosticName(nameKey)}");
                    }

                    _log.Info(
                        $"Buff OCR: reason={reason}, key={nameKey}, active={match.IsActive}, " +
                        $"stateConfidence={match.StateConfidence:0.000}, seconds={read.RemainingSeconds?.ToString() ?? "none"}, " +
                        $"bounds={FormatRect(read.Bounds)}, text={read.RecognizedText}");
                }
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
                var needsFastRetry = _statusObservations.NeedsFastRetry;
                _nextMonitorValueRecognitionAt = needsFastRetry
                    ? DateTimeOffset.UtcNow
                    : DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
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
