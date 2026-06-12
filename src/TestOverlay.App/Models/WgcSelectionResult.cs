using Windows.Graphics.Capture;

namespace TestOverlay.App.Models;

public sealed record WgcSelectionResult(
    string DisplayName,
    int Width,
    int Height,
    GraphicsCaptureItem Item,
    bool? LooksLikeMabinogiOverride = null)
{
    public bool LooksLikeMabinogi =>
        LooksLikeMabinogiOverride ??
        DisplayName.Contains("Mabinogi", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("\uB9C8\uBE44\uB178\uAE30", StringComparison.OrdinalIgnoreCase);
}
