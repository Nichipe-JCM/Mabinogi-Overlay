using Windows.Graphics.Capture;

namespace TestOverlay.App.Models;

public sealed record WgcSelectionResult(string DisplayName, int Width, int Height, GraphicsCaptureItem Item)
{
    public bool LooksLikeMabinogi =>
        DisplayName.Contains("Mabinogi", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("마비노기", StringComparison.OrdinalIgnoreCase);
}
