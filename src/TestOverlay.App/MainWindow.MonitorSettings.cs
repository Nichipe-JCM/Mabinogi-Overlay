using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private void BuffMonitorEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _buffMonitorEnabled = BuffMonitorEnabledCheckBox.IsChecked == true;
        if (!_buffMonitorEnabled)
        {
            if (_monitorDetectionMode == MonitorDetectionMode.BuffWindow)
            {
                SetMonitorDetectionMode(MonitorDetectionMode.None);
            }
            _recognizedBuffNameKeys.Clear();
            _selectedBuffNameKeys.Clear();
            _pendingInitialBuffMinuteValidation.Clear();
            _buffMonitorRoi = null;
            _buffIconMatches.Clear();
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, _buffMonitorEnabled);
        SynchronizeAlertNotificationElement();
        UpdateMonitorControlAvailability();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        if (_buffMonitorEnabled)
        {
            DetectBuffWindowButton_Click(DetectBuffWindowButton, new RoutedEventArgs());
        }
    }

    private void TuairimMonitorEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _tuairimMonitorEnabled = TuairimMonitorEnabledCheckBox.IsChecked == true;
        if (!_tuairimMonitorEnabled && _monitorDetectionMode == MonitorDetectionMode.Tuairim)
        {
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }
        if (!_tuairimMonitorEnabled)
        {
            _tuairimMonitorRoi = null;
            _tuairimAnchor = null;
            ResetTuairimPercentRecognitionState();
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, _tuairimMonitorEnabled);
        SynchronizeAlertNotificationElement();
        UpdateMonitorControlAvailability();
        RefreshInternalTimerOverlay();
        if (_tuairimMonitorEnabled)
        {
            DetectTuairimUiButton_Click(DetectTuairimUiButton, new RoutedEventArgs());
        }
    }

    private void MonitorTestModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTestMode && _monitorTestScenario == 1)
        {
            ExitMonitorTestMode();
        }
        else
        {
            SwitchMonitorTestMode(1);
        }
    }

    private void MonitorTestMode2Button_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTestMode && _monitorTestScenario == 2)
        {
            ExitMonitorTestMode();
        }
        else
        {
            SwitchMonitorTestMode(2);
        }
    }

    private void SwitchMonitorTestMode(int scenario)
    {
        if (_monitorTestMode)
        {
            ExitMonitorTestMode();
        }
        EnterMonitorTestMode(scenario);
    }

    private void EnterMonitorTestMode(int scenario)
    {
        FlushProfileAutoSave();
        _monitorTestPreviousBuffEnabled = _buffMonitorEnabled;
        _monitorTestPreviousTuairimEnabled = _tuairimMonitorEnabled;
        _monitorTestPreviousRecognizedBuffs = _recognizedBuffNameKeys.ToArray();
        _monitorTestPreviousSelectedBuffs = _selectedBuffNameKeys.ToArray();
        _monitorTestPreviousLayoutCanvasHeight = _layoutCanvasHeight;
        _monitorTestMode = true;
        _monitorTestScenario = scenario;
        _monitorValueRecognitionGeneration++;
        SetMonitorDetectionMode(MonitorDetectionMode.None);

        _buffMonitorEnabled = true;
        _tuairimMonitorEnabled = true;
        BuffMonitorEnabledCheckBox.IsChecked = true;
        TuairimMonitorEnabledCheckBox.IsChecked = true;
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _recognizedBuffNameKeys.Add("monitor.buff.battle.overture");
        _recognizedBuffNameKeys.Add("monitor.buff.march.song");
        _selectedBuffNameKeys.Add("monitor.buff.battle.overture");
        _selectedBuffNameKeys.Add("monitor.buff.march.song");

        _internalBuffTimers.Clear();
        var battleSeconds = scenario == 2 ? 32 : 35;
        var marchSeconds = scenario == 2 ? 34 : 40;
        _internalBuffTimers.Add(new InternalBuffTimer("monitor.buff.battle.overture", battleSeconds));
        _internalBuffTimers.Add(new InternalBuffTimer("monitor.buff.march.song", marchSeconds) { HasHarmony = true });
        _statusObservations.SetTuairimPercentForTest(scenario == 2 ? 89 : 88);
        _tuairimAlertFired = false;
        _monitorTestTuairimChargeSeconds = scenario == 2 ? TuairimNormalChargeSecondsPerPercent - 1 : 0;
        _monitorTestTuairimFullSeconds = 0;

        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, enabled: true, scheduleAutoSave: false);
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, enabled: true, scheduleAutoSave: false);
        SynchronizeAlertNotificationElement(scheduleAutoSave: false);
        UpdateBuffSelectionCheckStates();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        _internalTimerOverlayWindow?.SetTuairimPercent(_statusObservations.TuairimPercent);
        if (_overlayRuntime.IsRunning)
        {
            _internalTimerDebugTimer.Start();
        }
        UpdateMonitorControlAvailability();
        UpdateMonitorTestButtonPresentation();
        _log.Info(
            $"Monitor test mode {scenario} started: Battle Overture={battleSeconds}s, " +
            $"March Song[Harmony]={marchSeconds}s, Tuairim={_statusObservations.TuairimPercent}%.");
        SetStatus("monitor.test.started");
    }

    private void ExitMonitorTestMode()
    {
        _monitorTestMode = false;
        _monitorTestScenario = 0;
        _monitorValueRecognitionGeneration++;
        _internalBuffTimers.Clear();
        ResetTuairimPercentRecognitionState();
        _statusObservations.SetTuairimPercentForTest(0);
        _monitorTestTuairimChargeSeconds = 0;
        _monitorTestTuairimFullSeconds = 0;

        _buffMonitorEnabled = _monitorTestPreviousBuffEnabled;
        _tuairimMonitorEnabled = _monitorTestPreviousTuairimEnabled;
        BuffMonitorEnabledCheckBox.IsChecked = _buffMonitorEnabled;
        TuairimMonitorEnabledCheckBox.IsChecked = _tuairimMonitorEnabled;
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        foreach (var nameKey in _monitorTestPreviousRecognizedBuffs)
        {
            _recognizedBuffNameKeys.Add(nameKey);
        }
        foreach (var nameKey in _monitorTestPreviousSelectedBuffs)
        {
            _selectedBuffNameKeys.Add(nameKey);
        }
        _monitorTestPreviousRecognizedBuffs = [];
        _monitorTestPreviousSelectedBuffs = [];

        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, _buffMonitorEnabled, scheduleAutoSave: false);
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, _tuairimMonitorEnabled, scheduleAutoSave: false);
        SynchronizeAlertNotificationElement(scheduleAutoSave: false);
        _layoutCanvasHeight = _monitorTestPreviousLayoutCanvasHeight;
        UpdateBuffSelectionCheckStates();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        if (!_overlayRuntime.IsRunning)
        {
            _internalTimerDebugTimer.Stop();
        }
        else
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.MinValue;
            _ = SynchronizeMonitorValuesAsync("test-mode-ended");
        }
        UpdateMonitorControlAvailability();
        UpdateMonitorTestButtonPresentation();
        _log.Info("Monitor test mode stopped and temporary monitor values were cleared.");
        SetStatus("monitor.test.stopped");
    }

    private void UpdateMonitorTestButtonPresentation()
    {
        if (MonitorTestModeButton is null || MonitorTestMode2Button is null)
        {
            return;
        }
        MonitorTestModeButton.Content = L.T(
            _monitorTestMode && _monitorTestScenario == 1 ? "monitor.test.stop" : "monitor.test.start");
        MonitorTestMode2Button.Content = L.T(
            _monitorTestMode && _monitorTestScenario == 2 ? "monitor.test.stop" : "monitor.test.start.second");
    }

    private void AlertNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void BuffAlertSecondsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var settings = _alertAudio.Settings;
        settings.BuffAlertSeconds = ReadAlertThreshold(
            BuffAlertSecondsBox.Text,
            5,
            30,
            settings.BuffAlertSeconds);
        BuffAlertSecondsBox.Text = settings.BuffAlertSeconds.ToString();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertPercentBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var settings = _alertAudio.Settings;
        settings.TuairimAlertPercent = ReadAlertThreshold(
            TuairimAlertPercentBox.Text,
            90,
            100,
            settings.TuairimAlertPercent);
        TuairimAlertPercentBox.Text = settings.TuairimAlertPercent.ToString();
        ScheduleProfileAutoSave();
    }

    private void BuffAlertVolumeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox || !TryReadSoundSlotTagIndex(textBox.Tag, out var index))
        {
            return;
        }
        _alertAudio.SetBuffVolume(index, ReadAlertVolume(textBox.Text, _alertAudio.GetBuffVolume(index)));
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertVolumeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var settings = _alertAudio.Settings;
        settings.TuairimVolume = ReadAlertVolume(TuairimAlertVolumeBox.Text, settings.TuairimVolume);
        TuairimAlertVolumeBox.Text = settings.TuairimVolume.ToString();
        ScheduleProfileAutoSave();
    }

    private void BuffAlertSoundModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMonitorAlertSettings ||
            BuffAlertSoundModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }
        _alertAudio.Settings.BuffSoundMode = mode == MonitorAlertSettings.IndividualMode
            ? MonitorAlertSettings.IndividualMode
            : MonitorAlertSettings.GlobalMode;
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertFrequencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMonitorAlertSettings ||
            TuairimAlertFrequencyCombo.SelectedItem is not ComboBoxItem { Tag: string frequency })
        {
            return;
        }
        _alertAudio.Settings.TuairimFrequency = frequency == MonitorAlertSettings.EveryPercentFrequency
            ? MonitorAlertSettings.EveryPercentFrequency
            : MonitorAlertSettings.OnceFrequency;
        _tuairimAlertFired = false;
        ScheduleProfileAutoSave();
    }

    private void BrowseBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSoundSlotIndex(sender, out var index))
        {
            return;
        }
        var path = ChooseMonitorAlertSound();
        if (path is null)
        {
            return;
        }
        _alertAudio.SetBuffPath(index, path);
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void BrowseTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseMonitorAlertSound();
        if (path is null)
        {
            return;
        }
        _alertAudio.Settings.TuairimPath = path;
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void ClearBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSoundSlotIndex(sender, out var index))
        {
            return;
        }
        _alertAudio.ClearBuff(index);
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void ClearTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e)
    {
        _alertAudio.ClearTuairim();
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TestBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryReadSoundSlotIndex(sender, out var index))
        {
            _alertAudio.TestBuff(index);
        }
    }

    private void TestTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e) =>
        _alertAudio.TestTuairim();

    private string? ChooseMonitorAlertSound()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("monitor.alert.sound.choose"),
            Filter = L.T("erin.audio.file.filter"),
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static bool TryReadSoundSlotIndex(object sender, out int index)
    {
        index = -1;
        return sender is Button { Tag: var tag } &&
               int.TryParse(tag?.ToString(), out index) &&
               index is >= 0 and < 4;
    }

    private static bool TryReadSoundSlotTagIndex(object? tag, out int index)
    {
        index = -1;
        return int.TryParse(tag?.ToString(), out index) && index is >= 0 and < 4;
    }

    private static int ReadAlertThreshold(string? text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, out var value) && value >= minimum && value <= maximum ? value : fallback;

    private static int ReadAlertVolume(string? text, int fallback) =>
        int.TryParse(text, out var value) && value is >= 0 and <= 100 ? value : fallback;

    private void CommitMonitorAlertThresholdInputs()
    {
        if (BuffAlertSecondsBox is null)
        {
            return;
        }
        var settings = _alertAudio.Settings;
        settings.BuffAlertSeconds = ReadAlertThreshold(
            BuffAlertSecondsBox.Text,
            5,
            30,
            settings.BuffAlertSeconds);
        settings.TuairimAlertPercent = ReadAlertThreshold(
            TuairimAlertPercentBox.Text,
            90,
            100,
            settings.TuairimAlertPercent);
        var volumeBoxes = new[] { BuffAlertVolumeBox1, BuffAlertVolumeBox2, BuffAlertVolumeBox3, BuffAlertVolumeBox4, BuffAlertVolumeBox5, BuffAlertVolumeBox6, BuffAlertVolumeBox7 };
        for (var index = 0; index < volumeBoxes.Length; index++)
        {
            _alertAudio.SetBuffVolume(
                index,
                ReadAlertVolume(volumeBoxes[index].Text, _alertAudio.GetBuffVolume(index)));
        }
        settings.TuairimVolume = ReadAlertVolume(TuairimAlertVolumeBox.Text, settings.TuairimVolume);
        BuffAlertSecondsBox.Text = settings.BuffAlertSeconds.ToString();
        TuairimAlertPercentBox.Text = settings.TuairimAlertPercent.ToString();
    }

    private void RefreshMonitorAlertSettingsControls()
    {
        if (BuffAlertSecondsBox is null)
        {
            return;
        }
        _isUpdatingMonitorAlertSettings = true;
        try
        {
            var settings = _alertAudio.Settings;
            BuffAlertSecondsBox.Text = settings.BuffAlertSeconds.ToString();
            TuairimAlertPercentBox.Text = settings.TuairimAlertPercent.ToString();
            SelectComboBoxTag(BuffAlertSoundModeCombo, settings.BuffSoundMode);
            SelectComboBoxTag(TuairimAlertFrequencyCombo, settings.TuairimFrequency);

            var individual = settings.BuffSoundMode == MonitorAlertSettings.IndividualMode;
            BuffAlertSoundLabel1.Text = individual
                ? L.T("monitor.buff.sound.battle")
                : L.T("monitor.alert.sound.mode.global");
            BuffAlertSoundRow2.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow3.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow4.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow5.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow6.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow7.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;

            var pathBoxes = new[] { BuffAlertSoundPathBox1, BuffAlertSoundPathBox2, BuffAlertSoundPathBox3, BuffAlertSoundPathBox4, BuffAlertSoundPathBox5, BuffAlertSoundPathBox6, BuffAlertSoundPathBox7 };
            var clearButtons = new[] { ClearBuffAlertSoundButton1, ClearBuffAlertSoundButton2, ClearBuffAlertSoundButton3, ClearBuffAlertSoundButton4, ClearBuffAlertSoundButton5, ClearBuffAlertSoundButton6, ClearBuffAlertSoundButton7 };
            var testButtons = new[] { TestBuffAlertSoundButton1, TestBuffAlertSoundButton2, TestBuffAlertSoundButton3, TestBuffAlertSoundButton4, TestBuffAlertSoundButton5, TestBuffAlertSoundButton6, TestBuffAlertSoundButton7 };
            var volumeBoxes = new[] { BuffAlertVolumeBox1, BuffAlertVolumeBox2, BuffAlertVolumeBox3, BuffAlertVolumeBox4, BuffAlertVolumeBox5, BuffAlertVolumeBox6, BuffAlertVolumeBox7 };
            for (var index = 0; index < pathBoxes.Length; index++)
            {
                var path = _alertAudio.GetBuffPath(index);
                pathBoxes[index].Text = path;
                volumeBoxes[index].Text = _alertAudio.GetBuffVolume(index).ToString();
                clearButtons[index].IsEnabled = !string.IsNullOrWhiteSpace(path);
                testButtons[index].IsEnabled = File.Exists(path);
            }

            TuairimAlertSoundPathBox.Text = settings.TuairimPath;
            TuairimAlertVolumeBox.Text = settings.TuairimVolume.ToString();
            ClearTuairimAlertSoundButton.IsEnabled = !string.IsNullOrWhiteSpace(settings.TuairimPath);
            TestTuairimAlertSoundButton.IsEnabled = File.Exists(settings.TuairimPath);
        }
        finally
        {
            _isUpdatingMonitorAlertSettings = false;
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private void LoadStartupAlertSettings()
    {
        try
        {
            var profile = _profileStore.Load(ReadSelectedProfileName());
            if (profile is null)
            {
                return;
            }
            ApplyMonitorAlertSettings(profile);
            RefreshMonitorAlertSettingsControls();
        }
        catch (Exception exception)
        {
            _log.Error("Failed to load startup monitor alert settings.", exception);
        }
    }

    private void ApplyMonitorAlertSettings(OverlayProfile profile)
    {
        _alertAudio.LoadProfile(profile);
        _tuairimAlertFired = false;
        foreach (var timer in _internalBuffTimers)
        {
            timer.AlertFired = timer.RemainingSeconds <= _alertAudio.Settings.BuffAlertSeconds;
        }
    }

    private void TryFireBuffAlert(InternalBuffTimer timer, int previousSeconds, int currentSeconds)
    {
        var threshold = _alertAudio.Settings.BuffAlertSeconds;
        if (currentSeconds > threshold)
        {
            timer.AlertFired = false;
            return;
        }
        if (timer.AlertFired || previousSeconds <= threshold)
        {
            return;
        }

        if (!_buffAlertsEnabled)
        {
            return;
        }

        timer.AlertFired = true;
        _log.Info(
            $"Buff alert threshold reached: key={timer.NameKey}, threshold={threshold}, " +
            $"previous={previousSeconds}, current={currentSeconds}");
        _internalTimerOverlayWindow?.ShowBuffAlert(timer.NameKey, currentSeconds);
        _alertAudio.PlayBuff(timer.NameKey, $"buff:{timer.NameKey}");
    }

    private void TryFireTuairimAlert(int previousPercent, int currentPercent, bool hadAcceptedObservation)
    {
        var settings = _alertAudio.Settings;
        if (!hadAcceptedObservation)
        {
            _tuairimAlertFired = currentPercent >= settings.TuairimAlertPercent;
            return;
        }
        if (currentPercent < settings.TuairimAlertPercent)
        {
            _tuairimAlertFired = false;
            return;
        }
        if (!_tuairimAlertsEnabled)
        {
            return;
        }
        if (settings.TuairimFrequency == MonitorAlertSettings.EveryPercentFrequency)
        {
            if (currentPercent > previousPercent && currentPercent <= 99)
            {
                _log.Info(
                    $"Tuairim incremental alert: threshold={settings.TuairimAlertPercent}, " +
                    $"previous={previousPercent}, current={currentPercent}");
                _internalTimerOverlayWindow?.ShowTuairimAlert(currentPercent);
                _alertAudio.PlayTuairim($"tuairim:{currentPercent}");
            }
            return;
        }
        if (_tuairimAlertFired || previousPercent >= settings.TuairimAlertPercent)
        {
            return;
        }

        _tuairimAlertFired = true;
        _log.Info(
            $"Tuairim alert threshold reached: threshold={settings.TuairimAlertPercent}, " +
            $"previous={previousPercent}, current={currentPercent}");
        _internalTimerOverlayWindow?.ShowTuairimAlert(currentPercent);
        _alertAudio.PlayTuairim("tuairim");
    }

    private void DetectBuffWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_buffMonitorEnabled || _overlayRuntime.IsRunning)
        {
            return;
        }
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        SetMonitorDetectionMode(MonitorDetectionMode.BuffWindow);
        SetStatus("monitor.buff.detect.drag");
    }

    private void DetectTuairimUiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_tuairimMonitorEnabled || _overlayRuntime.IsRunning)
        {
            return;
        }
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        SetMonitorDetectionMode(MonitorDetectionMode.Tuairim);
        SetStatus("monitor.tuairim.detect.drag");
    }

    private void BuffSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string nameKey } checkBox ||
            _overlayRuntime.IsRunning ||
            !_recognizedBuffNameKeys.Contains(nameKey))
        {
            UpdateBuffSelectionCheckStates();
            return;
        }

        if (checkBox.IsChecked == true)
        {
            if (!CanAddBuffSelection(nameKey))
            {
                checkBox.IsChecked = false;
                SetStatus("monitor.buff.selection.invalid");
                return;
            }

            _selectedBuffNameKeys.Add(nameKey);
        }
        else
        {
            _selectedBuffNameKeys.Remove(nameKey);
        }

        RefreshInternalTimerElementPreviews();
        ScheduleProfileAutoSave();
    }

    private void BuffIconsOnlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.BuffIconsOnly = BuffIconsOnlyCheckBox.IsChecked == true;
        ApplyBuffSelectionDisplayMode();
        try
        {
            _settingsStore.Save(_appSettings);
        }
        catch (Exception exception)
        {
            _log.Error("Buff icon-only preference could not be saved.", exception);
        }
    }

    private void ApplyBuffSelectionDisplayMode()
    {
        var iconsOnly = _appSettings.BuffIconsOnly;
        var visibility = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
        BattleOvertureBuffNameText.Visibility = visibility;
        MarchSongBuffNameText.Visibility = visibility;
        VivaceBuffNameText.Visibility = visibility;
        HarvestSongBuffNameText.Visibility = visibility;
        DivineLinkBuffNameText.Visibility = visibility;
        ConditionSupportBuffNameText.Visibility = visibility;
        PurificationWaveBuffNameText.Visibility = visibility;

        foreach (var checkBox in BuffSelectionCheckBoxes())
        {
            checkBox.MinWidth = 0;
            checkBox.Width = iconsOnly ? 64 : double.NaN;
            checkBox.Margin = iconsOnly
                ? new Thickness(0, 0, 6, 0)
                : new Thickness(0, 0, 18, 0);
        }
    }

    private void ApplyRecognizedBuffs(IEnumerable<string> nameKeys)
    {
        _recognizedBuffNameKeys.Clear();
        foreach (var key in nameKeys.Where(InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains))
        {
            _recognizedBuffNameKeys.Add(key);
        }

        _selectedBuffNameKeys.RemoveWhere(key => !_recognizedBuffNameKeys.Contains(key));
        UpdateMonitorControlAvailability();
        RefreshInternalTimerElementPreviews();
        ScheduleProfileAutoSave();
    }

    private bool CanAddBuffSelection(string nameKey)
    {
        if (_selectedBuffNameKeys.Contains(nameKey))
        {
            return true;
        }
        if (_selectedBuffNameKeys.Count >= 2)
        {
            return false;
        }
        if (_selectedBuffNameKeys.Count == 0)
        {
            return true;
        }

        var existingKey = _selectedBuffNameKeys.Single();
        if (!MonitoredBuffCatalog.IsMusicBuff(nameKey) || !MonitoredBuffCatalog.IsMusicBuff(existingKey))
        {
            return true;
        }

        return nameKey == MonitoredBuffCatalog.MarchSong || existingKey == MonitoredBuffCatalog.MarchSong;
    }

    private IEnumerable<CheckBox> BuffSelectionCheckBoxes()
    {
        yield return BattleOvertureBuffCheckBox;
        yield return MarchSongBuffCheckBox;
        yield return VivaceBuffCheckBox;
        yield return HarvestSongBuffCheckBox;
        yield return DivineLinkBuffCheckBox;
        yield return ConditionSupportBuffCheckBox;
        yield return PurificationWaveBuffCheckBox;
    }

    private void UpdateBuffSelectionCheckStates()
    {
        foreach (var checkBox in BuffSelectionCheckBoxes())
        {
            if (checkBox.Tag is not string nameKey)
            {
                continue;
            }

            checkBox.IsChecked = _selectedBuffNameKeys.Contains(nameKey);
            var statusText = DetectionStatusTextFor(nameKey);
            if (statusText is not null)
            {
                ApplyDetectionStatus(statusText, _recognizedBuffNameKeys.Contains(nameKey));
            }
        }
    }

    private TextBlock? DetectionStatusTextFor(string nameKey) => nameKey switch
    {
        MonitoredBuffCatalog.BattleOverture => BattleOvertureDetectionStatusText,
        MonitoredBuffCatalog.MarchSong => MarchSongDetectionStatusText,
        MonitoredBuffCatalog.Vivace => VivaceDetectionStatusText,
        MonitoredBuffCatalog.HarvestSong => HarvestSongDetectionStatusText,
        MonitoredBuffCatalog.DivineLink => DivineLinkDetectionStatusText,
        MonitoredBuffCatalog.ConditionSupport => ConditionSupportDetectionStatusText,
        MonitoredBuffCatalog.PurificationWave => PurificationWaveDetectionStatusText,
        _ => null
    };

    private static void ApplyDetectionStatus(TextBlock textBlock, bool detected)
    {
        textBlock.Text = detected ? "O" : "X";
        textBlock.Foreground = detected
            ? System.Windows.Media.Brushes.LimeGreen
            : System.Windows.Media.Brushes.IndianRed;
    }

    private void UpdateMonitorControlAvailability()
    {
        if (DetectBuffWindowButton is null)
        {
            return;
        }

        var baseEditable = !_overlayRuntime.IsRunning && !_isMonitorDetectionBusy;
        var settingsEditable = baseEditable &&
                               _monitorDetectionMode == MonitorDetectionMode.None &&
                               !_monitorTestMode;
        StartOverlayButton.IsEnabled = !_overlayRuntime.IsRunning;
        StopOverlayLayoutButton.IsEnabled = _overlayRuntime.IsRunning;
        BuffMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        TuairimMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        BuffAlertSettingsPanel.IsEnabled = settingsEditable;
        TuairimAlertSettingsPanel.IsEnabled = settingsEditable;
        DetectBuffWindowButton.IsEnabled = baseEditable &&
                                           !_monitorTestMode &&
                                           _buffMonitorEnabled &&
                                           _monitorDetectionMode is MonitorDetectionMode.None or MonitorDetectionMode.BuffWindow;
        DetectTuairimUiButton.IsEnabled = baseEditable &&
                                         !_monitorTestMode &&
                                         _tuairimMonitorEnabled &&
                                         _monitorDetectionMode is MonitorDetectionMode.None or MonitorDetectionMode.Tuairim;
        foreach (var checkBox in BuffSelectionCheckBoxes())
        {
            var nameKey = checkBox.Tag as string;
            checkBox.IsEnabled = settingsEditable &&
                                 _buffMonitorEnabled &&
                                 nameKey is not null &&
                                 _recognizedBuffNameKeys.Contains(nameKey);
        }
        UpdateBuffSelectionCheckStates();
    }
}
