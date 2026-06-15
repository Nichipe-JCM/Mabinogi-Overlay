using System.Windows;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Models;

public sealed class OverlaySlot
{
    public OverlaySlot(
        SlotCandidate source,
        Rect overlayRect,
        BitmapSource preview,
        double opacity = 1,
        double scale = 1,
        bool hasOpacityOverride = false)
    {
        Source = source;
        OverlayRect = overlayRect;
        Preview = preview;
        Opacity = Math.Clamp(opacity, 0, 1);
        Scale = Math.Clamp(scale, 0.1, 10);
        HasOpacityOverride = hasOpacityOverride;
    }

    public SlotCandidate Source { get; set; }

    public Rect OverlayRect { get; set; }

    public BitmapSource Preview { get; set; }

    public double Opacity { get; set; }

    public double Scale { get; set; }

    public bool HasOpacityOverride { get; set; }

    public double EffectiveOpacity(double defaultOpacity) =>
        Math.Clamp(HasOpacityOverride ? Opacity : defaultOpacity, 0, 1);
}
