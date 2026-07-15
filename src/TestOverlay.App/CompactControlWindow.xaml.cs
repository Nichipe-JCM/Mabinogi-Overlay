using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class CompactControlWindow : Window
{
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4));
    private static readonly Brush StoppedBrush = new SolidColorBrush(Color.FromRgb(0x78, 0x7D, 0x82));
    private readonly MainWindow _host;
    private bool _closingFromHost;
    private bool _refreshing;
    private string _erinAlarmSignature = string.Empty;

    public CompactControlWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Activated += (_, _) => RefreshState();
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
        Closing += Window_Closing;
        _host.CompactErinStateChanged += Host_CompactErinStateChanged;
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
            _host.CompactErinStateChanged -= Host_CompactErinStateChanged;
        };
        RefreshState();
    }

    public void RefreshState()
    {
        if (!IsLoaded && !IsInitialized)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var state = _host.GetCompactControlState();
            ProfileText.Text = L.F("compact.profile.arg", state.ProfileName);
            OverlayStateText.Text = L.T(state.IsOverlayRunning ? "compact.overlay.running" : "compact.overlay.stopped");
            OverlayStateDot.Fill = state.IsOverlayRunning ? RunningBrush : StoppedBrush;
            OverlayToggleButton.Content = L.T(state.IsOverlayRunning ? "Overlay stop" : "Overlay start");
            BuffAlertsEnabledCheckBox.IsChecked = state.BuffAlertsEnabled;
            TuairimAlertsEnabledCheckBox.IsChecked = state.TuairimAlertsEnabled;

            ApplyBuffState(BattleBuffCheckBox, BattleDetectionStatusText, state, MonitoredBuffCatalog.BattleOverture);
            ApplyBuffState(MarchBuffCheckBox, MarchDetectionStatusText, state, MonitoredBuffCatalog.MarchSong);
            ApplyBuffState(VivaceBuffCheckBox, VivaceDetectionStatusText, state, MonitoredBuffCatalog.Vivace);
            ApplyBuffState(HarvestBuffCheckBox, HarvestDetectionStatusText, state, MonitoredBuffCatalog.HarvestSong);
            ApplyBuffState(DivineLinkBuffCheckBox, DivineLinkDetectionStatusText, state, MonitoredBuffCatalog.DivineLink);
            ApplyBuffState(ConditionSupportBuffCheckBox, ConditionSupportDetectionStatusText, state, MonitoredBuffCatalog.ConditionSupport);
            ApplyBuffState(PurificationWaveBuffCheckBox, PurificationWaveDetectionStatusText, state, MonitoredBuffCatalog.PurificationWave);
            BuffConfigurationText.Text = L.T(state.IsBuffMonitorConfigured
                ? "compact.buff.configured"
                : "compact.buff.not.configured");
            TuairimConfigurationText.Text = L.T(state.IsTuairimMonitorConfigured
                ? "compact.tuairim.configured"
                : "compact.tuairim.not.configured");
            RefreshErinState();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void CloseFromHost()
    {
        _closingFromHost = true;
        Close();
    }

    private static void ApplyBuffState(CheckBox checkBox, TextBlock statusText, CompactControlState state, string key)
    {
        var detected = state.RecognizedBuffNameKeys.Contains(key);
        checkBox.IsChecked = state.SelectedBuffNameKeys.Contains(key);
        checkBox.IsEnabled = state.IsBuffMonitorConfigured && detected;
        statusText.Text = detected ? "O" : "X";
        statusText.Foreground = detected ? Brushes.LimeGreen : Brushes.IndianRed;
    }

    private async void OverlayToggleButton_Click(object sender, RoutedEventArgs e)
    {
        OverlayToggleButton.IsEnabled = false;
        try
        {
            var result = await _host.ToggleOverlayFromCompactAsync();
            RefreshState();
            NoticeText.Text = result is null ? string.Empty : L.T(result);
        }
        finally
        {
            OverlayToggleButton.IsEnabled = true;
        }
    }

    private async void BuffCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not CheckBox { Tag: string key } checkBox)
        {
            return;
        }

        checkBox.IsEnabled = false;
        string? result = null;
        try
        {
            result = await _host.SetCompactBuffSelectionAsync(key, checkBox.IsChecked == true);
        }
        finally
        {
            RefreshState();
        }
        NoticeText.Text = result is null ? string.Empty : L.T(result);
    }

    private void ManageLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.TryOpenLayoutManagerFromCompact())
        {
            NoticeText.Text = L.T("Stop the overlay before opening Manage Layout.");
        }
    }

    private void AlertEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        _host.SetCompactAlertsEnabled(
            BuffAlertsEnabledCheckBox.IsChecked == true,
            TuairimAlertsEnabledCheckBox.IsChecked == true);
    }

    private void Host_CompactErinStateChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.InvokeAsync(RefreshErinState);
        }
    }

    private void RefreshErinState()
    {
        var state = _host.GetCompactErinState();
        ErinGameTimeText.Text = state.GameTime;
        ErinRealTimeText.Text = state.RealTime;
        ErinNextAlarmText.Text = state.NextAlarm;

        var signature = string.Join('|', state.Alarms.Select(alarm =>
            $"{alarm.Id}:{alarm.Name}:{alarm.Time}:{alarm.Enabled}:{alarm.Repeat}"));
        if (signature == _erinAlarmSignature)
        {
            return;
        }

        _erinAlarmSignature = signature;
        ErinAlarmList.Children.Clear();
        if (state.Alarms.Count == 0)
        {
            ErinAlarmList.Children.Add(new TextBlock
            {
                Text = L.T("erin.no.alarms.registered"),
                Foreground = (Brush)FindResource("OverlayMutedBrush")
            });
            return;
        }

        foreach (var alarm in state.Alarms)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var enabled = new CheckBox
            {
                IsChecked = alarm.Enabled,
                Tag = alarm.Id,
                VerticalAlignment = VerticalAlignment.Center
            };
            enabled.Click += ErinAlarmEnabledCheckBox_Click;
            var name = new TextBlock
            {
                Text = alarm.Name,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var time = new TextBlock
            {
                Text = alarm.Repeat ? $"{alarm.Time} · {L.T("erin.repeat")}" : alarm.Time,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Cascadia Mono, Consolas")
            };
            Grid.SetColumn(enabled, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(time, 2);
            row.Children.Add(enabled);
            row.Children.Add(name);
            row.Children.Add(time);
            ErinAlarmList.Children.Add(row);
        }
    }

    private void ErinAlarmEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int alarmId } checkBox)
        {
            _host.SetCompactErinAlarmEnabled(alarmId, checkBox.IsChecked == true);
        }
    }

    private void FullModeButton_Click(object sender, RoutedEventArgs e) => _host.RestoreFullMode();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _host.Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingFromHost)
        {
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(_host.Close);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshState);

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The pointer state can change while a drag begins.
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}

public sealed record CompactControlState(
    string ProfileName,
    bool IsOverlayRunning,
    bool IsBuffMonitorConfigured,
    bool IsTuairimMonitorConfigured,
    bool BuffAlertsEnabled,
    bool TuairimAlertsEnabled,
    IReadOnlySet<string> RecognizedBuffNameKeys,
    IReadOnlySet<string> SelectedBuffNameKeys);
