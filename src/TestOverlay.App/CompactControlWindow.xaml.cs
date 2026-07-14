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

    public CompactControlWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Activated += (_, _) => RefreshState();
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
        Closing += Window_Closing;
        Closed += (_, _) => LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
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

            ApplyBuffState(BattleBuffCheckBox, state, "monitor.buff.battle.overture");
            ApplyBuffState(MarchBuffCheckBox, state, "monitor.buff.march.song");
            ApplyBuffState(VivaceBuffCheckBox, state, "monitor.buff.vivace");
            ApplyBuffState(HarvestBuffCheckBox, state, "monitor.buff.harvest.song");
            BuffConfigurationText.Text = L.T(state.IsBuffMonitorConfigured
                ? "compact.buff.configured"
                : "compact.buff.not.configured");
            TuairimConfigurationText.Text = L.T(state.IsTuairimMonitorConfigured
                ? "compact.tuairim.configured"
                : "compact.tuairim.not.configured");
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

    private static void ApplyBuffState(CheckBox checkBox, CompactControlState state, string key)
    {
        checkBox.IsChecked = state.SelectedBuffNameKeys.Contains(key);
        checkBox.IsEnabled = state.IsBuffMonitorConfigured && state.RecognizedBuffNameKeys.Contains(key);
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

    private void OpenErinTimerButton_Click(object sender, RoutedEventArgs e) => _host.OpenErinTimerFromCompact();

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
    IReadOnlySet<string> RecognizedBuffNameKeys,
    IReadOnlySet<string> SelectedBuffNameKeys);
