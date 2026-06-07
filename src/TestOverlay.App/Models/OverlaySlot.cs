using System.Windows;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Models;

public sealed class OverlaySlot
{
    public OverlaySlot(SlotCandidate source, Rect overlayRect, BitmapSource preview)
    {
        Source = source;
        OverlayRect = overlayRect;
        Preview = preview;
    }

    public SlotCandidate Source { get; set; }

    public Rect OverlayRect { get; set; }

    public BitmapSource Preview { get; set; }
}
