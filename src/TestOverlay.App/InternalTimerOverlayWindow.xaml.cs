using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TestOverlay.App.Models;
using TestOverlay.App.Native;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class InternalTimerOverlayWindow : Window
{
    private HwndSource? _source;
    private IReadOnlyList<InternalBuffTimer> _timers = [];

    public InternalTimerOverlayWindow(
        double width,
        double height,
        double defaultOpacity,
        OverlaySlot? timerSlot,
        OverlaySlot? tuairimSlot,
        IReadOnlyList<InternalBuffTimer> timers)
    {
        InitializeComponent();
        Width = Math.Max(120, width);
        Height = Math.Max(80, height);
        ConfigurePanel(
            TimerPanel,
            timerSlot,
            InternalBuffTimerPreviewRenderer.BaseWidth,
            InternalBuffTimerPreviewRenderer.BaseHeight,
            defaultOpacity);
        ConfigurePanel(
            TuairimPanel,
            tuairimSlot,
            TuairimGaugePreviewRenderer.BaseWidth,
            TuairimGaugePreviewRenderer.BaseHeight,
            defaultOpacity);
        Focusable = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        SetTimers(timers);
        SetTuairimPercent(0);
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
    }

    public void SetTuairimPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        TuairimPercentText.Text = $"{percent}%";
        TuairimGaugeFill.Width = Math.Max(0, (TuairimGaugePreviewRenderer.BaseWidth - 20) * percent / 100.0);
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
        foreach (var nameKey in InternalBuffTimerPreviewRenderer.BuffNameKeys)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 154 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = L.T(nameKey),
                FontSize = 12,
                Foreground = (Brush)FindResource("OverlayTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var time = new TextBlock
            {
                Text = timerByKey.TryGetValue(nameKey, out var timer) ? FormatTime(timer.RemainingSeconds) : "--:--",
                Margin = new Thickness(12, 0, 0, 0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = timer is not null && timer.RemainingSeconds <= 30
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
        Dispatcher.Invoke(RenderTimers);

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
