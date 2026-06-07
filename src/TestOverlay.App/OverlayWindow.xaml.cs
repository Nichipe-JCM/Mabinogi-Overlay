using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App;

public partial class OverlayWindow : Window
{
    public bool IsClickThroughConfigured { get; private set; }

    public bool IsNoActivateConfigured { get; private set; }

    public bool IsTopmostConfigured { get; private set; }

    public int AppliedExtendedStyle { get; private set; }

    public Exception? ClickThroughConfigurationException { get; private set; }

    public OverlayWindow(double width, double height, double opacity, IReadOnlyList<OverlaySlot> slots)
    {
        InitializeComponent();
        Width = Math.Max(120, width);
        Height = Math.Max(80, height);
        Opacity = opacity;
        Focusable = false;
        ShowActivated = false;
        Topmost = false;
        SourceInitialized += (_, _) =>
        {
            ConfigureClickThrough();
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };
        Loaded += (_, _) => ConfigureClickThrough();
        RenderSlots(slots);
    }

    public void RenderSlots(IReadOnlyList<OverlaySlot> slots)
    {
        OverlayCanvas.Children.Clear();
        foreach (var slot in slots)
        {
            var image = new Image
            {
                Width = slot.OverlayRect.Width,
                Height = slot.OverlayRect.Height,
                Source = slot.Preview,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(image, slot.OverlayRect.X);
            Canvas.SetTop(image, slot.OverlayRect.Y);
            OverlayCanvas.Children.Add(image);
        }
    }

    private void ConfigureClickThrough()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = Win32Methods.GetWindowLong(handle, Win32Methods.GwlExStyle);
            exStyle |= Win32Methods.WsExLayered |
                       Win32Methods.WsExTransparent |
                       Win32Methods.WsExToolWindow |
                       Win32Methods.WsExNoActivate |
                       Win32Methods.WsExTopmost;
            Win32Methods.SetWindowLong(handle, Win32Methods.GwlExStyle, exStyle);
            Win32Methods.SetWindowPos(
                handle,
                Win32Methods.HwndTopmost,
                0,
                0,
                0,
                0,
                Win32Methods.SwpNomove |
                Win32Methods.SwpNosize |
                Win32Methods.SwpNoactivate |
                Win32Methods.SwpFramechanged);
            AppliedExtendedStyle = Win32Methods.GetWindowLong(handle, Win32Methods.GwlExStyle);
            IsClickThroughConfigured =
                HasStyle(AppliedExtendedStyle, Win32Methods.WsExLayered) &&
                HasStyle(AppliedExtendedStyle, Win32Methods.WsExTransparent);
            IsNoActivateConfigured = HasStyle(AppliedExtendedStyle, Win32Methods.WsExNoActivate);
            IsTopmostConfigured = HasStyle(AppliedExtendedStyle, Win32Methods.WsExTopmost);
        }
        catch (Exception ex)
        {
            ClickThroughConfigurationException = ex;
            IsClickThroughConfigured = false;
            IsNoActivateConfigured = false;
            IsTopmostConfigured = Topmost;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmMouseActivate)
        {
            handled = true;
            return Win32Methods.MaNoActivate;
        }

        if (msg == Win32Methods.WmNcHitTest)
        {
            handled = true;
            return Win32Methods.HtTransparent;
        }

        return nint.Zero;
    }

    private static bool HasStyle(int value, int style) => (value & style) == style;
}
