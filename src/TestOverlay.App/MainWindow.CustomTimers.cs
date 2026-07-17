using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private readonly ObservableCollection<CustomTimerDefinition> _customTimerDefinitions = [];
    private readonly Dictionary<int, ActiveCustomTimerState> _activeCustomTimers = [];
    private readonly Dictionary<int, MediaPlayer> _customTimerPlayers = [];
    private readonly DispatcherTimer _customTimerTick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CustomTimerDefinition? _editingCustomTimer;
    private bool _isUpdatingCustomTimerControls;

    private void InitializeCustomTimerFeature()
    {
        CustomTimerList.ItemsSource = _customTimerDefinitions;
        _customTimerTick.Tick += CustomTimerTick_Tick;
        _overlayRuntime.CustomTimerStartRequested += StartOrReloadCustomTimer;
        _overlayRuntime.CustomTimerCancelRequested += CancelCustomTimer;
        RefreshCustomTimerEditor();
        UpdateCustomTimerControlAvailability();
    }

    private void DisposeCustomTimerFeature()
    {
        _customTimerTick.Stop();
        _overlayRuntime.CustomTimerStartRequested -= StartOrReloadCustomTimer;
        _overlayRuntime.CustomTimerCancelRequested -= CancelCustomTimer;
        foreach (var player in _customTimerPlayers.Values)
        {
            player.Stop();
            player.Close();
        }
        _customTimerPlayers.Clear();
    }

    private void AddCustomTimerButton_Click(object sender, RoutedEventArgs e)
    {
        CommitCustomTimerEditor();
        var id = _customTimerDefinitions.Select(timer => timer.Id).DefaultIfEmpty(0).Max() + 1;
        var timer = new CustomTimerDefinition
        {
            Id = id,
            Name = L.F("custom.timer.default.name", id),
            Enabled = id == 1,
            StartHotkey = id == 1 ? "Ctrl+Shift+F6" : string.Empty,
            CancelHotkey = id == 1 ? "Ctrl+Shift+F7" : string.Empty
        };
        _customTimerDefinitions.Add(timer);
        CustomTimerList.SelectedItem = timer;
        EnsureMonitorElementPlaced(OverlayElementKind.CustomTimer, scheduleAutoSave: false);
        EnsureMonitorElementPlaced(OverlayElementKind.AlertNotification, scheduleAutoSave: false);
        ScheduleProfileAutoSave();
    }

    private void RemoveCustomTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomTimerList.SelectedItem is not CustomTimerDefinition timer)
        {
            return;
        }

        CancelCustomTimer(timer.Id);
        _customTimerDefinitions.Remove(timer);
        if (_customTimerDefinitions.Count == 0)
        {
            _overlaySlots.RemoveAll(slot => slot.Kind == OverlayElementKind.CustomTimer);
            _hiddenMonitorElementKinds.Add(OverlayElementKind.CustomTimer);
            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
        }
        CustomTimerList.SelectedItem = _customTimerDefinitions.FirstOrDefault();
        ScheduleProfileAutoSave();
    }

    private void CustomTimerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CommitCustomTimerEditor();
        _editingCustomTimer = CustomTimerList.SelectedItem as CustomTimerDefinition;
        RefreshCustomTimerEditor();
    }

    private void CustomTimerEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitCustomTimerEditor();

    private void CustomTimerOptionCheckBox_Click(object sender, RoutedEventArgs e) => CommitCustomTimerEditor();

    private void CustomTimerOverlayVisibleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var visible = CustomTimerOverlayVisibleCheckBox.IsChecked == true;
        SetMonitorElementVisibility(OverlayElementKind.CustomTimer, visible);
        RefreshInternalTimerOverlay();
    }

    private void BrowseCustomTimerSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCustomTimer is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Audio files|*.wav;*.mp3;*.wma;*.m4a|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            CustomTimerSoundPathBox.Text = dialog.FileName;
            CommitCustomTimerEditor();
        }
    }

    private void ClearCustomTimerSoundButton_Click(object sender, RoutedEventArgs e)
    {
        CustomTimerSoundPathBox.Text = string.Empty;
        CommitCustomTimerEditor();
    }

    private void TestCustomTimerSoundButton_Click(object sender, RoutedEventArgs e)
    {
        CommitCustomTimerEditor();
        if (_editingCustomTimer is not null)
        {
            PlayCustomTimerSound(_editingCustomTimer);
        }
    }

    private void CommitCustomTimerEditor()
    {
        if (_isUpdatingCustomTimerControls || _editingCustomTimer is null)
        {
            return;
        }

        _editingCustomTimer.Name = string.IsNullOrWhiteSpace(CustomTimerNameBox.Text)
            ? L.F("custom.timer.default.name", _editingCustomTimer.Id)
            : CustomTimerNameBox.Text.Trim();
        _editingCustomTimer.Enabled = CustomTimerEnabledCheckBox.IsChecked == true;
        _editingCustomTimer.DurationSeconds = ReadCustomTimerNumber(CustomTimerDurationBox.Text, 1, 86400, 60);
        _editingCustomTimer.AlertBeforeSeconds = ReadCustomTimerNumber(
            CustomTimerAlertBeforeBox.Text,
            0,
            _editingCustomTimer.DurationSeconds,
            Math.Min(10, _editingCustomTimer.DurationSeconds));
        _editingCustomTimer.StartHotkey = CustomTimerStartHotkeyBox.Text.Trim();
        _editingCustomTimer.CancelHotkey = CustomTimerCancelHotkeyBox.Text.Trim();
        _editingCustomTimer.VisualAlertEnabled = CustomTimerVisualAlertCheckBox.IsChecked == true;
        _editingCustomTimer.SoundAlertEnabled = CustomTimerSoundAlertCheckBox.IsChecked == true;
        _editingCustomTimer.SoundPath = CustomTimerSoundPathBox.Text.Trim();
        _editingCustomTimer.Volume = ReadCustomTimerNumber(CustomTimerVolumeBox.Text, 0, 100, 100);
        SynchronizeMonitorElementDimensions();
        if (_editingCustomTimer.VisualAlertEnabled)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.AlertNotification, scheduleAutoSave: false);
        }
        CustomTimerDurationBox.Text = _editingCustomTimer.DurationSeconds.ToString();
        CustomTimerAlertBeforeBox.Text = _editingCustomTimer.AlertBeforeSeconds.ToString();
        CustomTimerVolumeBox.Text = _editingCustomTimer.Volume.ToString();
        CustomTimerList.Items.Refresh();
        ScheduleProfileAutoSave();
    }

    private void RefreshCustomTimerEditor()
    {
        if (CustomTimerEditorPanel is null)
        {
            return;
        }

        _isUpdatingCustomTimerControls = true;
        try
        {
            var timer = _editingCustomTimer;
            CustomTimerEditorPanel.IsEnabled = timer is not null;
            CustomTimerEditorPanel.Visibility = timer is null ? Visibility.Collapsed : Visibility.Visible;
            CustomTimerEmptyText.Visibility = timer is null ? Visibility.Visible : Visibility.Collapsed;
            CustomTimerNameBox.Text = timer?.Name ?? string.Empty;
            CustomTimerEnabledCheckBox.IsChecked = timer?.Enabled == true;
            CustomTimerDurationBox.Text = timer?.DurationSeconds.ToString() ?? "60";
            CustomTimerAlertBeforeBox.Text = timer?.AlertBeforeSeconds.ToString() ?? "10";
            CustomTimerStartHotkeyBox.Text = timer?.StartHotkey ?? string.Empty;
            CustomTimerCancelHotkeyBox.Text = timer?.CancelHotkey ?? string.Empty;
            CustomTimerVisualAlertCheckBox.IsChecked = timer?.VisualAlertEnabled == true;
            CustomTimerSoundAlertCheckBox.IsChecked = timer?.SoundAlertEnabled == true;
            CustomTimerSoundPathBox.Text = timer?.SoundPath ?? string.Empty;
            CustomTimerVolumeBox.Text = timer?.Volume.ToString() ?? "100";
            CustomTimerOverlayVisibleCheckBox.IsChecked =
                !_hiddenMonitorElementKinds.Contains(OverlayElementKind.CustomTimer);
        }
        finally
        {
            _isUpdatingCustomTimerControls = false;
        }
        UpdateCustomTimerControlAvailability();
    }

    private void UpdateCustomTimerControlAvailability()
    {
        if (CustomTimerEditorPanel is null)
        {
            return;
        }

        var editable = !_overlayRuntime.IsRunning;
        AddCustomTimerButton.IsEnabled = editable;
        RemoveCustomTimerButton.IsEnabled = editable && _editingCustomTimer is not null;
        CustomTimerEditorPanel.IsEnabled = editable && _editingCustomTimer is not null;
        CustomTimerOverlayVisibleCheckBox.IsEnabled = editable && _customTimerDefinitions.Count > 0;
    }

    private static int ReadCustomTimerNumber(string text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);

    private void StartOrReloadCustomTimer(int timerId)
    {
        var definition = _customTimerDefinitions.FirstOrDefault(timer => timer.Id == timerId && timer.Enabled);
        if (definition is null || !_overlayRuntime.IsRunning)
        {
            return;
        }

        DismissCustomTimerAlert(timerId);
        _activeCustomTimers[timerId] = new ActiveCustomTimerState(
            definition,
            DateTimeOffset.UtcNow.AddSeconds(definition.DurationSeconds));
        _customTimerTick.Start();
        RefreshCustomTimerOverlay();
        _log.Info($"Custom timer armed: id={timerId}, name={definition.Name}, seconds={definition.DurationSeconds}");
    }

    private void CancelCustomTimer(int timerId)
    {
        DismissCustomTimerAlert(timerId);
        if (_activeCustomTimers.Remove(timerId))
        {
            RefreshCustomTimerOverlay();
            _log.Info($"Custom timer canceled without alert: id={timerId}");
        }
        if (_activeCustomTimers.Count == 0)
        {
            _customTimerTick.Stop();
        }
    }

    private void CustomTimerTick_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var state in _activeCustomTimers.Values.ToArray())
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((state.EndsAt - now).TotalSeconds));
            if (!state.AlertFired && remaining <= state.Definition.AlertBeforeSeconds && remaining > 0)
            {
                state.AlertFired = true;
                if (state.Definition.VisualAlertEnabled)
                {
                    _internalTimerOverlayWindow?.ShowCustomTimerAlert(
                        state.Definition.Id,
                        state.Definition.Name,
                        remaining);
                }
                if (state.Definition.SoundAlertEnabled)
                {
                    PlayCustomTimerSound(state.Definition);
                }
            }

            if (remaining == 0)
            {
                DismissCustomTimerAlert(state.Definition.Id);
                _activeCustomTimers.Remove(state.Definition.Id);
            }
        }

        if (_activeCustomTimers.Count == 0)
        {
            _customTimerTick.Stop();
        }
        RefreshCustomTimerOverlay();
    }

    private void RefreshCustomTimerOverlay()
    {
        var now = DateTimeOffset.UtcNow;
        var displays = _activeCustomTimers.Values
            .OrderBy(state => state.EndsAt)
            .Select(state => new ActiveCustomTimerDisplay(
                state.Definition.Id,
                state.Definition.Name,
                Math.Max(0, (int)Math.Ceiling((state.EndsAt - now).TotalSeconds)),
                state.AlertFired))
            .ToArray();
        _internalTimerOverlayWindow?.SetCustomTimers(displays);
        RefreshCompactControlState();
    }

    private void StopCustomTimers()
    {
        _customTimerTick.Stop();
        foreach (var timerId in _activeCustomTimers.Keys.ToArray())
        {
            DismissCustomTimerAlert(timerId);
        }
        _activeCustomTimers.Clear();
        RefreshCustomTimerOverlay();
    }

    private void DismissCustomTimerAlert(int timerId)
    {
        _internalTimerOverlayWindow?.DismissCustomTimerAlert(timerId);
        if (_customTimerPlayers.Remove(timerId, out var player))
        {
            player.Stop();
            player.Close();
        }
    }

    private void PlayCustomTimerSound(CustomTimerDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.SoundPath) || !File.Exists(definition.SoundPath))
        {
            return;
        }

        try
        {
            if (!_customTimerPlayers.TryGetValue(definition.Id, out var player))
            {
                player = new MediaPlayer();
                player.MediaFailed += (_, args) =>
                    _log.Error($"Custom timer audio failed: id={definition.Id}.", args.ErrorException);
                _customTimerPlayers[definition.Id] = player;
            }
            player.Stop();
            player.Close();
            player.Open(new Uri(definition.SoundPath, UriKind.Absolute));
            player.Volume = Math.Clamp(definition.Volume, 0, 100) / 100.0;
            player.Play();
        }
        catch (Exception exception)
        {
            _log.Error($"Custom timer audio failed: id={definition.Id}.", exception);
        }
    }

    private sealed class ActiveCustomTimerState(CustomTimerDefinition definition, DateTimeOffset endsAt)
    {
        public CustomTimerDefinition Definition { get; } = definition;
        public DateTimeOffset EndsAt { get; } = endsAt;
        public bool AlertFired { get; set; }
    }
}
