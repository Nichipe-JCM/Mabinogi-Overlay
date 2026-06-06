using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App;

public partial class OverlayWindow : Window
{
    public OverlayWindow(double width, double height, double opacity, IReadOnlyList<OverlaySlot> slots)
    {
        InitializeComponent();
        Width = Math.Max(120, width);
        Height = Math.Max(80, height);
        Opacity = opacity;
        Loaded += (_, _) => ConfigureClickThrough();
        RenderSlots(slots);
    }

    private void RenderSlots(IReadOnlyList<OverlaySlot> slots)
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
        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Methods.GetWindowLong(handle, Win32Methods.GwlExStyle);
        exStyle |= Win32Methods.WsExLayered |
                   Win32Methods.WsExTransparent |
                   Win32Methods.WsExToolWindow |
                   Win32Methods.WsExNoActivate |
                   Win32Methods.WsExTopmost;
        Win32Methods.SetWindowLong(handle, Win32Methods.GwlExStyle, exStyle);
    }
}
