using System.Windows;

namespace TestOverlay.App;

public partial class MainWindow
{
    private CompactControlWindow? _compactControlWindow;

    internal event EventHandler? CompactErinStateChanged
    {
        add => ErinTimerPanel.CompactStateChanged += value;
        remove => ErinTimerPanel.CompactStateChanged -= value;
    }

    internal CompactControlState GetCompactControlState() => new(
        ReadSelectedProfileName(),
        _overlayRuntime.IsRunning,
        _buffMonitorRoi is not null && _buffIconMatches.Count > 0,
        _tuairimAnchor is not null,
        _buffMonitorEnabled,
        _tuairimMonitorEnabled,
        _recognizedBuffNameKeys.ToHashSet(StringComparer.Ordinal),
        _selectedBuffNameKeys.ToHashSet(StringComparer.Ordinal));

    internal ErinCompactState GetCompactErinState() => ErinTimerPanel.GetCompactState();

    internal void SetCompactErinAlarmEnabled(int alarmId, bool enabled) =>
        ErinTimerPanel.SetAlarmEnabled(alarmId, enabled);

    internal void SetCompactMonitorEnabled(bool buffEnabled, bool tuairimEnabled)
    {
        SetBuffMonitorEnabled(buffEnabled, beginDetectionIfMissing: false);
        SetTuairimMonitorEnabled(tuairimEnabled, beginDetectionIfMissing: false);
        RefreshCompactControlState();
    }

    internal async Task<string?> ToggleOverlayFromCompactAsync()
    {
        if (_overlayRuntime.IsRunning)
        {
            StopOverlay();
        }
        else
        {
            await StartOverlayAsync();
        }

        RefreshCompactControlState();
        return !_overlayRuntime.IsRunning && !string.IsNullOrWhiteSpace(_lastStatusMessage)
            ? _lastStatusMessage
            : null;
    }

    internal async Task<string?> SetCompactBuffSelectionAsync(string nameKey, bool selected)
    {
        if (!_buffMonitorEnabled || !_recognizedBuffNameKeys.Contains(nameKey))
        {
            return "compact.buff.not.configured";
        }

        if (selected)
        {
            if (!CanAddBuffSelection(nameKey))
            {
                return "monitor.buff.selection.invalid";
            }

            _selectedBuffNameKeys.Add(nameKey);
        }
        else
        {
            _selectedBuffNameKeys.Remove(nameKey);
        }

        UpdateBuffSelectionCheckStates();
        RefreshInternalTimerElementPreviews();
        ScheduleProfileAutoSave();
        if (_overlayRuntime.IsRunning)
        {
            StopOverlay(setStatus: false);
            await StartOverlayAsync();
        }
        else
        {
            RefreshInternalTimerOverlay();
        }
        RefreshCompactControlState();
        return null;
    }

    internal bool TryOpenLayoutManagerFromCompact()
    {
        if (_overlayRuntime.IsRunning)
        {
            return false;
        }

        if (_compactControlWindow is null)
        {
            return false;
        }

        OpenLayoutEditor(_compactControlWindow);
        return true;
    }

    internal void RestoreFullMode()
    {
        _appSettings.CompactModeEnabled = false;
        SaveCompactModePreference();
        var compactBounds = _compactControlWindow is { IsVisible: true } compact
            ? new Rect(compact.Left, compact.Top, compact.ActualWidth, compact.ActualHeight)
            : Rect.Empty;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        if (!compactBounds.IsEmpty)
        {
            Left = compactBounds.Left + (compactBounds.Width - Width) / 2;
            Top = compactBounds.Top + (compactBounds.Height - Height) / 2;
        }
        Show();
        Activate();
        _compactControlWindow?.Hide();
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e) => EnterCompactMode(savePreference: true);

    private void EnterCompactMode(bool savePreference)
    {
        FlushProfileAutoSave();
        if (savePreference)
        {
            _appSettings.CompactModeEnabled = true;
            SaveCompactModePreference();
        }

        _compactControlWindow ??= new CompactControlWindow(this);
        if (IsVisible)
        {
            _compactControlWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _compactControlWindow.Left = Left + (ActualWidth - _compactControlWindow.Width) / 2;
            _compactControlWindow.Top = Top + (ActualHeight - _compactControlWindow.Height) / 2;
        }
        _compactControlWindow.RefreshState();
        _compactControlWindow.Show();
        _compactControlWindow.WindowState = WindowState.Normal;
        _compactControlWindow.Activate();
        Hide();
    }

    private void SaveCompactModePreference()
    {
        try
        {
            _settingsStore.Save(_appSettings);
        }
        catch (Exception exception)
        {
            _log.Error("Compact mode preference could not be saved.", exception);
        }
    }

    private void RefreshCompactControlState() => _compactControlWindow?.RefreshState();

    private void CloseCompactControlWindow()
    {
        _compactControlWindow?.CloseFromHost();
        _compactControlWindow = null;
    }
}
