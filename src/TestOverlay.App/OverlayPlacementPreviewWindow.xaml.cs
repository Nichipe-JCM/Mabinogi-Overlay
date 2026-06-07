using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class OverlayPlacementPreviewWindow : Window
{
    private readonly IReadOnlyList<OverlaySlot> _slots;
    private readonly Action<double, double, double, double> _placementChanged;

    public OverlayPlacementPreviewWindow(
        double left,
        double top,
        double width,
        double height,
        double opacity,
        IReadOnlyList<OverlaySlot> slots,
        Action<double, double, double, double> placementChanged)
    {
        InitializeComponent();
        _slots = slots;
        _placementChanged = placementChanged;
        Left = left;
        Top = top;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        Opacity = Math.Clamp(opacity, 0.35, 1);
        RenderSlots();
        LocationChanged += (_, _) => NotifyPlacementChanged();
        SizeChanged += (_, _) =>
        {
            RenderSlots();
            NotifyPlacementChanged();
        };
    }

    private void RenderSlots()
    {
        PreviewCanvas.Children.Clear();
        PreviewCanvas.Width = Width;
        PreviewCanvas.Height = Height;

        foreach (var slot in _slots)
        {
            var image = new Image
            {
                Source = slot.Preview,
                Width = slot.OverlayRect.Width,
                Height = slot.OverlayRect.Height,
                Stretch = Stretch.Fill,
                Opacity = 0.9,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(image, slot.OverlayRect.X);
            Canvas.SetTop(image, slot.OverlayRect.Y);
            PreviewCanvas.Children.Add(image);
        }
    }

    private void PreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        NotifyPlacementChanged();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
        NotifyPlacementChanged();
    }

    private void NotifyPlacementChanged() =>
        _placementChanged(Math.Round(Left), Math.Round(Top), Math.Round(Width), Math.Round(Height));
}
