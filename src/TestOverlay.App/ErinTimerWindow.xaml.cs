using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TestOverlay.App;

public partial class ErinTimerWindow : UserControl, IDisposable
{
    private const int RealSecondsPerGameDay = 36 * 60;
    private const int GameSecondsPerDay = 24 * 60 * 60;
    private const int GameTimeScale = GameSecondsPerDay / RealSecondsPerGameDay;
    private const int DisplayStepSeconds = 10 * 60;
    private const uint FlashAll = 3;
    private const uint FlashTimerNoForeground = 12;

    private readonly ErinTimerSettingsStore _store = new();
    private AppLog? _log;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly ErinTimerSettings _settings;
    private bool _initializing = true;
    private long? _previousAbsoluteGameSeconds;

    public ErinTimerWindow()
    {
        _settings = _store.Load();
        InitializeComponent();
        DataContext = this;

        HourCombo.ItemsSource = Enumerable.Range(0, 24).Select(value => value.ToString("00")).ToList();
        MinuteCombo.ItemsSource = Enumerable.Range(0, 6).Select(value => (value * 10).ToString("00")).ToList();
        HourCombo.SelectedIndex = 0;
        MinuteCombo.SelectedIndex = 0;
        DesktopNotificationsCheckBox.IsChecked = _settings.DesktopNotifications;
        VolumeSlider.Value = _settings.IsMuted ? 0 : _settings.Volume;
        _mediaPlayer.Volume = _settings.IsMuted ? 0 : _settings.Volume;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        RefreshAlarmRows();
        RefreshStaticState();
        UpdateClock(checkAlarms: false);
        _initializing = false;

        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
    }

    public ObservableCollection<ErinAlarmRow> Alarms { get; } = [];
    public event Action<string>? NoticeRequested;

    public void AttachLog(AppLog log)
    {
        _log = log;
        _log.Info($"Erin timer initialized: alarms={_settings.Alarms.Count}, settings={_store.SettingsPath}");
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
        SaveSettings();
        _log?.Info("Erin timer disposed.");
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            RefreshAlarmRows();
            RefreshStaticState();
            UpdateClock(checkAlarms: false);
        });

    private void ClockTimer_Tick(object? sender, EventArgs e) => UpdateClock(checkAlarms: true);

    private void UpdateClock(bool checkAlarms)
    {
        var time = GetTimeInfo();
        GameTimeText.Text = FormatGameTime(time.DisplayGameSeconds);
        RealTimeText.Text = L.F("erin.real.time.arg", time.Now.ToString("HH:mm:ss"));

        if (checkAlarms)
        {
            CheckAlarms(time.AbsoluteGameSeconds);
        }

        UpdateNextAlarm(time.RawGameSeconds);
    }

    private ErinTimeInfo GetTimeInfo()
    {
        var now = DateTimeOffset.Now;
        var corrected = now - TimeSpan.FromSeconds(_settings.SyncDelaySeconds);
        var midnight = new DateTimeOffset(corrected.Year, corrected.Month, corrected.Day, 0, 0, 0, corrected.Offset);
        var secondsSinceMidnight = (corrected - midnight).TotalSeconds;
        var cycleIndex = (int)(secondsSinceMidnight / RealSecondsPerGameDay);
        var secondsInCycle = secondsSinceMidnight % RealSecondsPerGameDay;
        var rawGameSeconds = (int)(secondsInCycle * GameTimeScale) % GameSecondsPerDay;
        var displayGameSeconds = rawGameSeconds / DisplayStepSeconds * DisplayStepSeconds;
        var gameDayKey = (long)DateOnly.FromDateTime(corrected.DateTime).DayNumber * 40 + cycleIndex;
        return new ErinTimeInfo(now, rawGameSeconds, displayGameSeconds, gameDayKey * GameSecondsPerDay + rawGameSeconds);
    }

    private void CheckAlarms(long currentAbsoluteGameSeconds)
    {
        if (_previousAbsoluteGameSeconds is null ||
            currentAbsoluteGameSeconds <= _previousAbsoluteGameSeconds ||
            currentAbsoluteGameSeconds - _previousAbsoluteGameSeconds > DisplayStepSeconds)
        {
            _previousAbsoluteGameSeconds = currentAbsoluteGameSeconds;
            return;
        }

        var previous = _previousAbsoluteGameSeconds.Value;
        _previousAbsoluteGameSeconds = currentAbsoluteGameSeconds;
        var firstGameDay = previous / GameSecondsPerDay;
        var lastGameDay = currentAbsoluteGameSeconds / GameSecondsPerDay;
        var changed = false;

        foreach (var alarm in _settings.Alarms.Where(alarm => alarm.Enabled).ToList())
        {
            var targetSeconds = alarm.Hour * 3600L + alarm.Minute * 60L;
            for (var gameDay = firstGameDay; gameDay <= lastGameDay; gameDay++)
            {
                var target = gameDay * GameSecondsPerDay + targetSeconds;
                if (target <= previous || target > currentAbsoluteGameSeconds)
                {
                    continue;
                }

                FireAlarm(alarm);
                if (!alarm.Repeat)
                {
                    alarm.Enabled = false;
                    changed = true;
                }

                break;
            }
        }

        if (changed)
        {
            SaveSettings();
            RefreshAlarmRows();
        }
    }

    private void FireAlarm(ErinAlarm alarm)
    {
        var alarmTime = $"{alarm.Hour:00}:{alarm.Minute:00}";
        FlashTaskbar();
        if (_settings.DesktopNotifications)
        {
            TryShowToast(L.T("erin.timer"), L.F("erin.alarm.reached.arg", alarmTime));
        }

        var audioFile = alarm.CustomSoundEnabled && File.Exists(alarm.CustomAudioFile)
            ? alarm.CustomAudioFile
            : _settings.AudioFile;
        PlayAudio(audioFile);
        _log?.Info($"Erin alarm fired: id={alarm.Id}, name={alarm.Name}, time={alarmTime}, repeat={alarm.Repeat}, customSound={alarm.CustomSoundEnabled}");
    }

    private void PlayAudio(string? audioFile)
    {
        if (string.IsNullOrWhiteSpace(audioFile) || !File.Exists(audioFile))
        {
            return;
        }

        try
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            _mediaPlayer.Open(new Uri(audioFile, UriKind.Absolute));
            _mediaPlayer.Volume = _settings.IsMuted ? 0 : _settings.Volume;
            _mediaPlayer.Play();
        }
        catch (Exception exception)
        {
            _log?.Error("Failed to play Erin timer audio.", exception);
        }
    }

    private void TryShowToast(string title, string message)
    {
        try
        {
            var toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var textNodes = toastXml.GetElementsByTagName("text");
            textNodes[0].AppendChild(toastXml.CreateTextNode(title));
            textNodes[1].AppendChild(toastXml.CreateTextNode(message));
            ToastNotificationManager.CreateToastNotifier("Mabinogi Overlay").Show(new ToastNotification(toastXml));
        }
        catch (Exception exception)
        {
            _log?.Error("Failed to show Erin timer desktop notification.", exception);
        }
    }

    private void FlashTaskbar()
    {
        var hostWindow = Window.GetWindow(this);
        if (hostWindow is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(hostWindow).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashAll | FlashTimerNoForeground,
            Count = 0,
            Timeout = 0
        };
        _ = FlashWindowEx(ref info);
    }

    private void AddAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (HourCombo.SelectedItem is not string hourText || MinuteCombo.SelectedItem is not string minuteText ||
            !int.TryParse(hourText, out var hour) || !int.TryParse(minuteText, out var minute))
        {
            return;
        }

        if (_settings.Alarms.Any(alarm => alarm.Hour == hour && alarm.Minute == minute))
        {
            NoticeRequested?.Invoke(L.T("erin.duplicate.alarm.message"));
            return;
        }

        var alarm = new ErinAlarm
        {
            Id = _settings.Alarms.Count == 0 ? 1 : _settings.Alarms.Max(item => item.Id) + 1,
            Name = NewAlarmNameBox.Text.Trim(),
            Hour = hour,
            Minute = minute,
            Repeat = NewAlarmRepeatCheckBox.IsChecked == true,
            Enabled = true
        };
        _settings.Alarms.Add(alarm);
        SortAlarms();
        SaveSettings();
        RefreshAlarmRows();
        AlarmListBox.SelectedItem = Alarms.FirstOrDefault(row => row.Id == alarm.Id);
    }

    private void UpdateAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (AlarmListBox.SelectedItem is not ErinAlarmRow selected ||
            HourCombo.SelectedItem is not string hourText || MinuteCombo.SelectedItem is not string minuteText ||
            !int.TryParse(hourText, out var hour) || !int.TryParse(minuteText, out var minute))
        {
            NoticeRequested?.Invoke(L.T("erin.select.alarm.update.message"));
            return;
        }

        if (_settings.Alarms.Any(alarm => alarm.Id != selected.Id && alarm.Hour == hour && alarm.Minute == minute))
        {
            NoticeRequested?.Invoke(L.T("erin.duplicate.alarm.message"));
            return;
        }

        selected.Model.Name = NewAlarmNameBox.Text.Trim();
        selected.Model.Hour = hour;
        selected.Model.Minute = minute;
        selected.Model.Repeat = NewAlarmRepeatCheckBox.IsChecked == true;
        SortAlarms();
        SaveSettings();
        RefreshAlarmRows(selected.Id);
    }

    private void AlarmListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlarmListBox.SelectedItem is not ErinAlarmRow selected)
        {
            return;
        }

        NewAlarmNameBox.Text = selected.Model.Name;
        HourCombo.SelectedItem = selected.Model.Hour.ToString("00");
        MinuteCombo.SelectedItem = selected.Model.Minute.ToString("00");
        NewAlarmRepeatCheckBox.IsChecked = selected.Model.Repeat;
    }

    private void NewAlarmNameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAlarmNamePlaceholder();

    private void NewAlarmNameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateAlarmNamePlaceholder();

    private void NewAlarmNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateAlarmNamePlaceholder();

    private void UpdateAlarmNamePlaceholder()
    {
        if (AlarmNamePlaceholder is null || NewAlarmNameBox is null)
        {
            return;
        }

        AlarmNamePlaceholder.Visibility = string.IsNullOrEmpty(NewAlarmNameBox.Text) && !NewAlarmNameBox.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AlarmListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AlarmListBox, e.OriginalSource as DependencyObject) is not ListBoxItem)
        {
            AlarmListBox.SelectedItem = null;
            NewAlarmNameBox.Clear();
            NewAlarmRepeatCheckBox.IsChecked = false;
        }
    }

    private void DeleteAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (AlarmListBox.SelectedItem is not ErinAlarmRow selected)
        {
            NoticeRequested?.Invoke(L.T("erin.select.alarm.message"));
            return;
        }

        _settings.Alarms.RemoveAll(alarm => alarm.Id == selected.Id);
        SaveSettings();
        RefreshAlarmRows();
    }

    private void HandleAlarmRowChanged()
    {
        SaveSettings();
        RefreshAlarmSummary();
        UpdateClock(checkAlarms: false);
    }

    private void ChooseAlarmSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int alarmId })
        {
            return;
        }

        var alarm = _settings.Alarms.FirstOrDefault(item => item.Id == alarmId);
        var file = alarm is null ? null : ChooseAudioFile();
        if (alarm is null || file is null)
        {
            return;
        }

        alarm.CustomAudioFile = file;
        alarm.CustomSoundEnabled = true;
        SaveSettings();
        RefreshAlarmRows(alarmId);
    }

    private void UseGlobalSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int alarmId })
        {
            return;
        }

        var alarm = _settings.Alarms.FirstOrDefault(item => item.Id == alarmId);
        if (alarm is null)
        {
            return;
        }

        alarm.CustomSoundEnabled = false;
        alarm.CustomAudioFile = null;
        SaveSettings();
        RefreshAlarmRows(alarmId);
    }

    private void ChooseGlobalSoundButton_Click(object sender, RoutedEventArgs e)
    {
        var file = ChooseAudioFile();
        if (file is null)
        {
            return;
        }

        _settings.AudioFile = file;
        SaveSettings();
        RefreshStaticState();
    }

    private string? ChooseAudioFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("erin.choose.audio.file"),
            Filter = L.T("erin.audio.file.filter"),
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.Volume = Math.Clamp(e.NewValue, 0, 1);
        if (_settings.Volume > 0)
        {
            _settings.PreMuteVolume = _settings.Volume;
            _settings.IsMuted = false;
        }
        else
        {
            _settings.IsMuted = true;
        }

        _mediaPlayer.Volume = _settings.IsMuted ? 0 : _settings.Volume;
        SaveSettings();
        RefreshVolumeState();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        if (_settings.IsMuted)
        {
            _settings.IsMuted = false;
            _settings.Volume = Math.Clamp(_settings.PreMuteVolume, 0.01, 1);
        }
        else
        {
            if (_settings.Volume > 0)
            {
                _settings.PreMuteVolume = _settings.Volume;
            }

            _settings.IsMuted = true;
            _settings.Volume = 0;
        }

        VolumeSlider.Value = _settings.Volume;
        _initializing = false;
        _mediaPlayer.Volume = _settings.IsMuted ? 0 : _settings.Volume;
        SaveSettings();
        RefreshVolumeState();
    }

    private void DesktopNotificationsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.DesktopNotifications = DesktopNotificationsCheckBox.IsChecked == true;
        SaveSettings();
    }

    private void StopSoundButton_Click(object sender, RoutedEventArgs e) => _mediaPlayer.Stop();

    private void SyncMinusButton_Click(object sender, RoutedEventArgs e) => ChangeSyncDelay(-0.5);

    private void SyncPlusButton_Click(object sender, RoutedEventArgs e) => ChangeSyncDelay(0.5);

    private void ChangeSyncDelay(double delta)
    {
        _settings.SyncDelaySeconds = Math.Clamp(_settings.SyncDelaySeconds + delta, -30, 30);
        _previousAbsoluteGameSeconds = null;
        SaveSettings();
        RefreshSyncDelayText();
        UpdateClock(checkAlarms: false);
    }

    private void RefreshAlarmRows(int? selectedId = null)
    {
        selectedId ??= (AlarmListBox.SelectedItem as ErinAlarmRow)?.Id;
        Alarms.Clear();
        foreach (var alarm in _settings.Alarms)
        {
            Alarms.Add(new ErinAlarmRow(alarm, HandleAlarmRowChanged));
        }

        AlarmListBox.SelectedItem = selectedId is null ? null : Alarms.FirstOrDefault(row => row.Id == selectedId);
        RefreshAlarmSummary();
    }

    private void RefreshAlarmSummary()
    {
        var total = _settings.Alarms.Count;
        var enabled = _settings.Alarms.Count(alarm => alarm.Enabled);
        var repeating = _settings.Alarms.Count(alarm => alarm.Enabled && alarm.Repeat);
        AlarmCountText.Text = L.F("erin.alarm.count.arg", total, enabled);
        AlarmStatusText.Text = total == 0
            ? L.T("erin.no.alarms.registered")
            : L.F("erin.alarm.status.arg", enabled, repeating);
    }

    private void RefreshStaticState()
    {
        AudioFileText.Text = string.IsNullOrWhiteSpace(_settings.AudioFile)
            ? L.T("erin.no.audio.file")
            : Path.GetFileName(_settings.AudioFile);
        DesktopNotificationsCheckBox.IsChecked = _settings.DesktopNotifications;
        RefreshSyncDelayText();
        RefreshVolumeState();
        RefreshAlarmSummary();
    }

    private void RefreshSyncDelayText()
    {
        SyncDelayText.Text = _settings.SyncDelaySeconds switch
        {
            > 0 => L.F("erin.sync.behind.arg", _settings.SyncDelaySeconds),
            < 0 => L.F("erin.sync.ahead.arg", Math.Abs(_settings.SyncDelaySeconds)),
            _ => L.T("erin.no.sync.correction")
        };
    }

    private void RefreshVolumeState()
    {
        VolumeText.Text = L.F("erin.volume.arg", (int)Math.Round((_settings.IsMuted ? 0 : _settings.Volume) * 100));
        MuteButton.Content = L.T(_settings.IsMuted ? "erin.unmute" : "erin.mute");
    }

    private void UpdateNextAlarm(int rawGameSeconds)
    {
        var enabled = _settings.Alarms.Where(alarm => alarm.Enabled).ToList();
        if (enabled.Count == 0)
        {
            NextAlarmText.Text = L.T("erin.no.enabled.alarms");
            return;
        }

        var next = enabled
            .Select(alarm => new
            {
                Alarm = alarm,
                RealSeconds = ((alarm.Hour * 3600 + alarm.Minute * 60 - rawGameSeconds + GameSecondsPerDay) % GameSecondsPerDay) / GameTimeScale
            })
            .OrderBy(item => item.RealSeconds)
            .First();
        var remaining = TimeSpan.FromSeconds(next.RealSeconds);
        NextAlarmText.Text = L.F(
            "erin.next.alarm.arg",
            next.Alarm.Hour,
            next.Alarm.Minute,
            next.Alarm.Repeat ? L.T("erin.repeat") : L.T("erin.once"),
            (int)remaining.TotalMinutes,
            remaining.Seconds);
    }

    private void SortAlarms() =>
        _settings.Alarms = _settings.Alarms.OrderBy(alarm => alarm.Hour).ThenBy(alarm => alarm.Minute).ToList();

    private void SaveSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception exception)
        {
            _log?.Error("Failed to save Erin timer settings.", exception);
        }
    }

    private void MediaPlayer_MediaFailed(object? sender, ExceptionEventArgs e) =>
        _log?.Error("Erin timer media playback failed.", e.ErrorException);

    private static string FormatGameTime(int gameSeconds) =>
        $"{gameSeconds / 3600:00}:{gameSeconds % 3600 / 60:00}";

    public sealed class ErinAlarmRow : INotifyPropertyChanged
    {
        private readonly Action _changed;

        public ErinAlarmRow(ErinAlarm model, Action changed)
        {
            Model = model;
            _changed = changed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ErinAlarm Model { get; }

        public int Id => Model.Id;

        public string Name => string.IsNullOrWhiteSpace(Model.Name)
            ? L.F("erin.alarm.default.name.arg", Model.Id)
            : Model.Name;

        public string TimeText => $"{Model.Hour:00}:{Model.Minute:00}";

        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value)
                {
                    return;
                }

                Model.Enabled = value;
                OnPropertyChanged(nameof(Enabled));
                _changed();
            }
        }

        public bool Repeat
        {
            get => Model.Repeat;
            set
            {
                if (Model.Repeat == value)
                {
                    return;
                }

                Model.Repeat = value;
                OnPropertyChanged(nameof(Repeat));
                _changed();
            }
        }

        public string SoundLabel => Model.CustomSoundEnabled && !string.IsNullOrWhiteSpace(Model.CustomAudioFile)
            ? Path.GetFileName(Model.CustomAudioFile)
            : L.T("erin.sound.global");

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record ErinTimeInfo(DateTimeOffset Now, int RawGameSeconds, int DisplayGameSeconds, long AbsoluteGameSeconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}
