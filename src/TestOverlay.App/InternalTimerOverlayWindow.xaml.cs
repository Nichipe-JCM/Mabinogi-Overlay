using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TestOverlay.App.Models;
using TestOverlay.App.Native;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class InternalTimerOverlayWindow : Window
{
    private HwndSource? _source;
    private IReadOnlyList<InternalBuffTimer> _timers = [];
    private IReadOnlyCollection<string> _visibleBuffNameKeys = [];
    private readonly Dictionary<string, ActiveAlert> _activeAlerts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _alertBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private double _alertPanelBaseOpacity = 1;
    private bool _alertPanelAvailable;
    private bool _alertBlinkVisible = true;

    public InternalTimerOverlayWindow(
        double width,
        double height,
        double defaultOpacity,
        OverlaySlot? timerSlot,
        OverlaySlot? tuairimSlot,
        OverlaySlot? alertSlot,
        IReadOnlyList<InternalBuffTimer> timers,
        IReadOnlyCollection<string> visibleBuffNameKeys)
    {
        InitializeComponent();
        Width = Math.Max(120, width);
        Height = Math.Max(80, height);
        ConfigurePanel(
            TimerPanel,
            timerSlot,
            InternalBuffTimerPreviewRenderer.BaseWidth,
            InternalBuffTimerPreviewRenderer.GetBaseHeight(visibleBuffNameKeys.Count),
            defaultOpacity);
        ConfigurePanel(
            TuairimPanel,
            tuairimSlot,
            TuairimGaugePreviewRenderer.BaseWidth,
            TuairimGaugePreviewRenderer.BaseHeight,
            defaultOpacity);
        ConfigureAlertPanel(alertSlot, defaultOpacity);
        Focusable = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        _visibleBuffNameKeys = visibleBuffNameKeys.ToArray();
        SetTimers(timers);
        SetTuairimPercent(0);
        _alertBlinkTimer.Tick += AlertBlinkTimer_Tick;
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
    }

    public void SetTuairimPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        TuairimPercentText.Text = $"{percent}%";
    }

    public void ShowBuffAlert(string nameKey, int remainingSeconds) =>
        ShowAlert(new ActiveAlert(
            $"buff:{nameKey}",
            nameKey,
            Math.Max(0, remainingSeconds),
            IsTuairim: false,
            DateTimeOffset.UtcNow.AddSeconds(5)));

    public void ShowTuairimAlert(int percent) =>
        ShowAlert(new ActiveAlert(
            "tuairim",
            null,
            Math.Clamp(percent, 0, 100),
            IsTuairim: true,
            DateTimeOffset.UtcNow.AddSeconds(5)));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        ApplyClickThroughStyles(handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _alertBlinkTimer.Stop();
        LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        base.OnClosed(e);
    }

    public void SetTimers(IReadOnlyList<InternalBuffTimer> timers)
    {
        _timers = timers;
        RenderTimers();
    }

    private void RenderTimers()
    {
        if (TimerRows is null)
        {
            return;
        }

        TimerRows.Children.Clear();
        var timerByKey = _timers.ToDictionary(timer => timer.NameKey, StringComparer.Ordinal);
        foreach (var nameKey in InternalBuffTimerPreviewRenderer.BuffNameKeys.Where(_visibleBuffNameKeys.Contains))
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 154 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = InternalBuffTimerPreviewRenderer.BuildDisplayName(
                    nameKey,
                    timerByKey.TryGetValue(nameKey, out var displayTimer) ? displayTimer : null),
                FontFamily = new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontSize = 11,
                Foreground = (Brush)FindResource("OverlayTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new Viewbox
            {
                Child = nameText,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                MaxHeight = 16
            };
            var time = new TextBlock
            {
                Text = displayTimer is not null ? FormatTime(displayTimer.RemainingSeconds) : "--:--",
                Margin = new Thickness(8, 0, 0, 0),
                FontFamily = new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = displayTimer is not null && displayTimer.RemainingSeconds <= 30
                    ? (Brush)FindResource("OverlayDangerBrush")
                    : (Brush)FindResource("OverlayAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 1);
            row.Children.Add(name);
            row.Children.Add(time);
            TimerRows.Children.Add(row);
        }

        TuairimLabel.Text = L.T("monitor.tuairim.overlay");
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            RenderTimers();
            RenderAlertRows();
        });

    private static string FormatTime(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

    private static void ConfigurePanel(
        FrameworkElement panel,
        OverlaySlot? slot,
        double baseWidth,
        double baseHeight,
        double defaultOpacity)
    {
        if (slot is null)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Clamp(
            Math.Min(slot.OverlayRect.Width / baseWidth, slot.OverlayRect.Height / baseHeight),
            0.1,
            10);
        panel.Width = baseWidth;
        panel.Height = baseHeight;
        panel.Opacity = slot.EffectiveOpacity(defaultOpacity);
        panel.LayoutTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(panel, slot.OverlayRect.X);
        Canvas.SetTop(panel, slot.OverlayRect.Y);
        panel.Visibility = Visibility.Visible;
    }

    private void ConfigureAlertPanel(OverlaySlot? slot, double defaultOpacity)
    {
        if (slot is null)
        {
            AlertPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Clamp(
            Math.Min(
                slot.OverlayRect.Width / AlertNotificationPreviewRenderer.BaseWidth,
                slot.OverlayRect.Height / AlertNotificationPreviewRenderer.BaseHeight),
            0.1,
            10);
        AlertPanel.Width = AlertNotificationPreviewRenderer.BaseWidth;
        _alertPanelBaseOpacity = slot.EffectiveOpacity(defaultOpacity);
        AlertPanel.LayoutTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(AlertPanel, slot.OverlayRect.X);
        Canvas.SetTop(AlertPanel, slot.OverlayRect.Y);
        AlertPanel.Visibility = Visibility.Collapsed;
        _alertPanelAvailable = true;
    }

    private void ShowAlert(ActiveAlert alert)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowAlert(alert));
            return;
        }
        if (!_alertPanelAvailable)
        {
            return;
        }

        _activeAlerts[alert.Id] = alert;
        _alertBlinkVisible = true;
        RenderAlertRows();
        AlertPanel.Opacity = _alertPanelBaseOpacity;
        AlertPanel.Visibility = Visibility.Visible;
        if (!_alertBlinkTimer.IsEnabled)
        {
            _alertBlinkTimer.Start();
        }
    }

    private void AlertBlinkTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _activeAlerts.Values
            .Where(alert => alert.ExpiresAt <= now)
            .Select(alert => alert.Id)
            .ToArray();
        foreach (var id in expired)
        {
            _activeAlerts.Remove(id);
        }

        if (_activeAlerts.Count == 0)
        {
            _alertBlinkTimer.Stop();
            AlertPanel.Visibility = Visibility.Collapsed;
            AlertRows.Children.Clear();
            return;
        }

        if (expired.Length > 0)
        {
            RenderAlertRows();
        }
        _alertBlinkVisible = !_alertBlinkVisible;
        AlertPanel.Opacity = _alertPanelBaseOpacity * (_alertBlinkVisible ? 1 : 0.22);
    }

    private void RenderAlertRows()
    {
        if (AlertRows is null)
        {
            return;
        }

        AlertRows.Children.Clear();
        foreach (var alert in _activeAlerts.Values)
        {
            var message = alert.IsTuairim
                ? L.F("monitor.alert.visual.tuairim", alert.Value)
                : L.F("monitor.alert.visual.buff", L.T(alert.NameKey!), alert.Value);
            AlertRows.Children.Add(new TextBlock
            {
                Text = message,
                Height = AlertNotificationPreviewRenderer.RowHeight,
                Foreground = (Brush)FindResource("OverlayDangerBrush"),
                FontFamily = new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    private sealed record ActiveAlert(
        string Id,
        string? NameKey,
        int Value,
        bool IsTuairim,
        DateTimeOffset ExpiresAt);

    private static void ApplyClickThroughStyles(nint handle)
    {
        if (handle == nint.Zero)
        {
            return;
        }

        var style = Win32Methods.GetWindowLongPtrSafe(handle, Win32Methods.GwlExStyle);
        style |= Win32Methods.WsExLayered;
        style |= Win32Methods.WsExTransparent;
        style |= Win32Methods.WsExToolWindow;
        style |= Win32Methods.WsExNoActivate;
        style |= Win32Methods.WsExTopmost;
        Win32Methods.SetWindowLongPtrSafe(handle, Win32Methods.GwlExStyle, style);
        Win32Methods.SetWindowPosSafe(
            handle,
            Win32Methods.HwndTopmost,
            0,
            0,
            0,
            0,
            Win32Methods.SwpNomove |
            Win32Methods.SwpNosize |
            Win32Methods.SwpNoactivate |
            Win32Methods.SwpFramechanged |
            Win32Methods.SwpShowWindow);
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmNcHitTest)
        {
            handled = true;
            return new nint(Win32Methods.HtTransparent);
        }

        if (msg == Win32Methods.WmMouseActivate)
        {
            handled = true;
            return new nint(Win32Methods.MaNoActivate);
        }

        return nint.Zero;
    }
}
