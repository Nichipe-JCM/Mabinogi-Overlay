using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App;

public partial class OverlayWindow : Window
{
    private readonly Image _compositedImage = new()
    {
        Stretch = Stretch.Fill,
        Focusable = false,
        IsHitTestVisible = false
    };
    private HwndSource? _source;

    public bool IsClickThroughConfigured { get; private set; }

    public bool IsNoActivateConfigured { get; private set; }

    public bool IsTopmostConfigured { get; private set; }

    public bool IsInputHookConfigured { get; private set; }

    public nint AppliedExtendedStyle { get; private set; }

    public Exception? ClickThroughConfigurationException { get; private set; }

    public OverlayWindow(double width, double height, double opacity, IReadOnlyList<OverlaySlot> slots)
    {
        InitializeComponent();

        Width = Math.Max(120, width);
        Height = Math.Max(80, height);
        Opacity = Math.Clamp(opacity, 0, 1);

        Focusable = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;

        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(ApplyClickThroughStyles, DispatcherPriority.ApplicationIdle);
        };

        RenderSlots(slots);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);

        if (_source is not null)
        {
            _source.AddHook(WndProc);
            IsInputHookConfigured = true;
        }

        ApplyClickThroughStyles();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        base.OnClosed(e);
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
                Opacity = Math.Clamp(slot.Opacity, 0.05, 1),
                Focusable = false,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(image, slot.OverlayRect.X);
            Canvas.SetTop(image, slot.OverlayRect.Y);
            OverlayCanvas.Children.Add(image);
        }
    }

    public void RenderCompositedFrame(BitmapSource frame)
    {
        if (OverlayCanvas.Children.Count != 1 || !ReferenceEquals(OverlayCanvas.Children[0], _compositedImage))
        {
            OverlayCanvas.Children.Clear();
            OverlayCanvas.Children.Add(_compositedImage);
        }

        _compositedImage.Width = Width;
        _compositedImage.Height = Height;
        _compositedImage.Source = frame;
        Canvas.SetLeft(_compositedImage, 0);
        Canvas.SetTop(_compositedImage, 0);
    }

    private void ApplyClickThroughStyles()
    {
        try
        {
            ClickThroughConfigurationException = null;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == nint.Zero)
            {
                return;
            }

            var exStyle = Win32Methods.GetWindowLongPtrSafe(handle, Win32Methods.GwlExStyle);
            exStyle |= Win32Methods.WsExLayered;
            exStyle |= Win32Methods.WsExTransparent;
            exStyle |= Win32Methods.WsExToolWindow;
            exStyle |= Win32Methods.WsExNoActivate;
            exStyle |= Win32Methods.WsExTopmost;

            Win32Methods.SetWindowLongPtrSafe(handle, Win32Methods.GwlExStyle, exStyle);

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

            AppliedExtendedStyle = Win32Methods.GetWindowLongPtrSafe(handle, Win32Methods.GwlExStyle);

            IsClickThroughConfigured =
                HasStyle(AppliedExtendedStyle, Win32Methods.WsExLayered) &&
                HasStyle(AppliedExtendedStyle, Win32Methods.WsExTransparent);
            IsNoActivateConfigured = HasStyle(AppliedExtendedStyle, Win32Methods.WsExNoActivate);
            IsTopmostConfigured = HasStyle(AppliedExtendedStyle, Win32Methods.WsExTopmost);

            if (!IsClickThroughConfigured || !IsNoActivateConfigured || !IsTopmostConfigured)
            {
                throw new InvalidOperationException(
                    "Overlay HWND style incomplete. " +
                    $"exStyle=0x{AppliedExtendedStyle.ToInt64():X16}, " +
                    $"clickThrough={IsClickThroughConfigured}, " +
                    $"noActivate={IsNoActivateConfigured}, " +
                    $"topmost={IsTopmostConfigured}");
            }
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

    private static bool HasStyle(nint value, int style) =>
        (value.ToInt64() & style) == style;
}
