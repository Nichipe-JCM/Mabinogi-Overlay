namespace TestOverlay.App.Models;

public sealed class OverlayProfile
{
    public string Name { get; set; } = "default";

    public double CanvasWidth { get; set; } = 360;

    public double CanvasHeight { get; set; } = 160;

    public double ScreenLeft { get; set; } = 120;

    public double ScreenTop { get; set; } = 120;

    public double Opacity { get; set; } = 1;

    public string StopHotkey { get; set; } = "Ctrl+Shift+F8";

    public int RefreshIntervalMs { get; set; } = 33;

    public int RefreshFps { get; set; } = 30;

    public double LayoutSlotScale { get; set; } = 1.5;

    public double GridSnapSize { get; set; } = 10;

    public int SlotInnerSize { get; set; } = 29;

    public int SlotInnerWidth { get; set; } = 29;

    public int SlotInnerHeight { get; set; } = 29;

    public int SelectedSectionPattern { get; set; }

    public List<OverlayProfileSectionSettings> SectionSettings { get; set; } = [];

    public List<OverlayProfileCandidate> Candidates { get; set; } = [];

    public List<OverlayProfileSection> Sections { get; set; } = [];

    public List<OverlayProfileSlot> Slots { get; set; } = [];
}

public sealed class OverlayProfileSectionSettings
{
    public int PatternIndex { get; set; }

    public string PatternName { get; set; } = string.Empty;

    public double SmallGapX { get; set; } = 1;

    public double SmallGapY { get; set; } = 5;

    public double LargeGap { get; set; } = 16;
}

public sealed class OverlayProfileSlot
{
    public int SourceCandidateId { get; set; }

    public double SourceX { get; set; }

    public double SourceY { get; set; }

    public double SourceWidth { get; set; }

    public double SourceHeight { get; set; }

    public double OverlayX { get; set; }

    public double OverlayY { get; set; }

    public double OverlayWidth { get; set; }

    public double OverlayHeight { get; set; }

    public double Opacity { get; set; } = 1;

    public double Scale { get; set; } = 1;
}

public sealed class OverlayProfileCandidate
{
    public int Id { get; set; }

    public double SourceX { get; set; }

    public double SourceY { get; set; }

    public double SourceWidth { get; set; }

    public double SourceHeight { get; set; }

    public double Score { get; set; }

    public bool IsSelected { get; set; }
}

public sealed class OverlayProfileSection
{
    public int Id { get; set; }

    public int SeedCandidateId { get; set; }

    public int PatternIndex { get; set; }

    public double SmallGapX { get; set; }

    public double SmallGapY { get; set; }

    public double LargeGap { get; set; }

    public List<int> CandidateIds { get; set; } = [];
}
