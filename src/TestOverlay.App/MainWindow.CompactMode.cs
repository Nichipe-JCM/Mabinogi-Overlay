using System.Windows;

namespace TestOverlay.App;

public partial class MainWindow
{
    private CompactControlWindow? _compactControlWindow;

    internal CompactControlState GetCompactControlState() => new(
        ReadSelectedProfileName(),
        _overlayRuntime.IsRunning,
        _buffMonitorEnabled && _buffMonitorRoi is not null,
        _tuairimMonitorEnabled && _tuairimAnchor is not null,
        _recognizedBuffNameKeys.ToHashSet(StringComparer.Ordinal),
        _selectedBuffNameKeys.ToHashSet(StringComparer.Ordinal));

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

        _compactControlWindow?.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            OpenLayoutEditorButton_Click(OpenLayoutEditorButton, new RoutedEventArgs());
            if (_appSettings.CompactModeEnabled)
            {
                EnterCompactMode(savePreference: false);
            }
        });
        return true;
    }

    internal void OpenErinTimerFromCompact()
    {
        RestoreFullMode();
        RightPanelTabs.SelectedItem = ErinTimerTabItem;
    }

    internal void RestoreFullMode()
    {
        _appSettings.CompactModeEnabled = false;
        SaveCompactModePreference();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _compactControlWindow?.Hide();
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e) => EnterCompactMode(savePreference: true);

    private void EnterCompactMode(bool savePreference)
    {
        if (savePreference)
        {
            _appSettings.CompactModeEnabled = true;
            SaveCompactModePreference();
        }

        _compactControlWindow ??= new CompactControlWindow(this);
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
