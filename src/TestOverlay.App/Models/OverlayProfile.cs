namespace TestOverlay.App.Models;

public sealed class OverlayProfile
{
    public string Name { get; set; } = "default";

    public double CanvasWidth { get; set; } = 360;

    public double CanvasHeight { get; set; } = 160;

    public double ScreenLeft { get; set; } = 120;

    public double ScreenTop { get; set; } = 120;

    public double Opacity { get; set; } = 0.8;

    public string StopHotkey { get; set; } = "Ctrl+Shift+F8";

    public List<OverlayProfileSlot> Slots { get; set; } = [];
}

public sealed class OverlayProfileSlot
{
    public double SourceX { get; set; }

    public double SourceY { get; set; }

    public double SourceWidth { get; set; }

    public double SourceHeight { get; set; }

    public double OverlayX { get; set; }

    public double OverlayY { get; set; }

    public double OverlayWidth { get; set; }

    public double OverlayHeight { get; set; }
}
