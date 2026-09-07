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
    private double _alertPanelScale = 1;
    private double _alertPanelAnchorBottom;
    private bool _alertPanelAvailable;
    private bool _customTimerPanelAvailable;
    private bool _alertBlinkVisible = true;
    private int _maxAlertRows = 2;

    public InternalTimerOverlayWindow(
        double width,
        double height,
        double defaultOpacity,
        OverlaySlot? timerSlot,
        OverlaySlot? tuairimSlot,
        OverlaySlot? alertSlot,
        OverlaySlot? customTimerSlot,
        int alertPreviewRows,
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
        ConfigureAlertPanel(alertSlot, defaultOpacity, alertPreviewRows);
        ConfigurePanel(
            CustomTimerPanel,
            customTimerSlot,
            CustomTimerPreviewRenderer.BaseWidth,
            customTimerSlot?.Source.SourceRect.Height ?? CustomTimerPreviewRenderer.GetBaseHeight(1),
            defaultOpacity);
        _customTimerPanelAvailable = customTimerSlot is not null;
        SetCustomTimers([]);
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
            null,
            Math.Max(0, remainingSeconds),
            AlertKind.Buff,
            DateTimeOffset.UtcNow.AddSeconds(5)));

    public void ShowTuairimAlert(int percent) =>
        ShowAlert(new ActiveAlert(
            "tuairim",
            null,
            null,
            Math.Clamp(percent, 0, 100),
            AlertKind.Tuairim,
            DateTimeOffset.UtcNow.AddSeconds(5)));

    public void ShowCustomTimerAlert(int timerId, string name, int remainingSeconds) =>
        ShowAlert(new ActiveAlert(
            $"custom:{timerId}",
            null,
            name,
            Math.Max(0, remainingSeconds),
            AlertKind.CustomTimer,
            DateTimeOffset.UtcNow.AddSeconds(5)));

    public void DismissCustomTimerAlert(int timerId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => DismissCustomTimerAlert(timerId));
            return;
        }

        if (!_activeAlerts.Remove($"custom:{timerId}"))
        {
            return;
        }

        if (_activeAlerts.Count == 0)
        {
            _alertBlinkTimer.Stop();
            AlertPanel.Visibility = Visibility.Collapsed;
            AlertRows.Children.Clear();
            return;
        }

        RenderAlertRows();
    }

    public void SetCustomTimers(IReadOnlyList<ActiveCustomTimerDisplay> timers)
    {
        CustomTimerRows.Children.Clear();
        foreach (var timer in timers)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 148 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = timer.Name,
                Foreground = (Brush)FindResource("OverlayTextBrush"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var time = new TextBlock
            {
                Text = FormatTime(timer.RemainingSeconds),
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = timer.IsAlerting
                    ? (Brush)FindResource("OverlayDangerBrush")
                    : (Brush)FindResource("OverlayAccentBrush"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
            CustomTimerRows.Children.Add(row);
        }

        CustomTimerPanel.Visibility = !_customTimerPanelAvailable
            ? Visibility.Collapsed
            : timers.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

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
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 76 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image
            {
                Source = BuffVisualCatalog.ActiveIcon(nameKey),
                Width = 18,
                Height = 18,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true
            };
            timerByKey.TryGetValue(nameKey, out var displayTimer);
            var badge = new TextBlock
            {
                Text = InternalBuffTimerPreviewRenderer.BuildBadge(displayTimer),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x75)),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var time = new TextBlock
            {
                Text = displayTimer is not null ? FormatTime(displayTimer.RemainingSeconds) : "--:--",
                Margin = new Thickness(4, 0, 0, 0),
                FontFamily = new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = displayTimer is not null && displayTimer.RemainingSeconds <= 30
                    ? (Brush)FindResource("OverlayDangerBrush")
                    : (Brush)FindResource("OverlayAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 1);
            Grid.SetColumn(time, 2);
            row.Children.Add(icon);
            row.Children.Add(badge);
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

    private void ConfigureAlertPanel(OverlaySlot? slot, double defaultOpacity, int alertPreviewRows)
    {
        _maxAlertRows = Math.Clamp(alertPreviewRows, 1, 4);
        if (slot is null)
        {
            AlertPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Clamp(
            Math.Min(
                slot.OverlayRect.Width / AlertNotificationPreviewRenderer.BaseWidth,
                slot.OverlayRect.Height / AlertNotificationPreviewRenderer.GetBaseHeight(alertPreviewRows)),
            0.1,
            10);
        _alertPanelScale = scale;
        _alertPanelAnchorBottom = slot.OverlayRect.Bottom;
        AlertPanel.Width = AlertNotificationPreviewRenderer.BaseWidth;
        AlertPanel.Height = AlertNotificationPreviewRenderer.GetBaseHeight(1);
        _alertPanelBaseOpacity = slot.EffectiveOpacity(defaultOpacity);
        AlertPanel.LayoutTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(AlertPanel, slot.OverlayRect.X);
        Canvas.SetTop(AlertPanel, _alertPanelAnchorBottom - AlertPanel.Height * scale);
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

        if (!_activeAlerts.ContainsKey(alert.Id) && _activeAlerts.Count >= _maxAlertRows)
        {
            var oldest = _activeAlerts.Values.OrderBy(item => item.ExpiresAt).First();
            _activeAlerts.Remove(oldest.Id);
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
            if (alert.Kind == AlertKind.Buff)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Height = AlertNotificationPreviewRenderer.RowHeight };
                row.Children.Add(new Image
                {
                    Source = BuffVisualCatalog.ActiveIcon(alert.NameKey!),
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.None,
                    SnapsToDevicePixels = true
                });
                row.Children.Add(AlertText(FormatTime(alert.Value), new Thickness(8, 0, 0, 0)));
                AlertRows.Children.Add(row);
                continue;
            }

            var message = alert.Kind == AlertKind.Tuairim
                ? L.F("monitor.alert.visual.tuairim", alert.Value)
                : L.F("custom.timer.visual.alert", alert.DisplayName ?? string.Empty, alert.Value);
            AlertRows.Children.Add(AlertText(message, new Thickness(0)));
        }

        var visibleRows = Math.Max(1, Math.Min(_maxAlertRows, _activeAlerts.Count));
        var panelHeight = AlertNotificationPreviewRenderer.GetBaseHeight(visibleRows);
        AlertPanel.Height = panelHeight;
        Canvas.SetTop(AlertPanel, _alertPanelAnchorBottom - panelHeight * _alertPanelScale);
    }

    private TextBlock AlertText(string text, Thickness margin) => new()
    {
        Text = text,
        Margin = margin,
        Height = AlertNotificationPreviewRenderer.RowHeight,
        Foreground = (Brush)FindResource("OverlayDangerBrush"),
        FontFamily = new FontFamily("Noto Sans KR, Malgun Gothic"),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    private sealed record ActiveAlert(
        string Id,
        string? NameKey,
        string? DisplayName,
        int Value,
        AlertKind Kind,
        DateTimeOffset ExpiresAt);

    private enum AlertKind
    {
        Buff,
        Tuairim,
        CustomTimer
    }

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
