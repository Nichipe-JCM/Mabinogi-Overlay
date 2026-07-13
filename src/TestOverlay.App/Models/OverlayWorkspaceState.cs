using System.Collections.ObjectModel;

namespace TestOverlay.App.Models;

public sealed class OverlayWorkspaceState
{
    public ObservableCollection<SlotCandidate> Candidates { get; } = [];

    public ObservableCollection<QuickslotSection> Sections { get; } = [];

    public List<OverlaySlot> OverlaySlots { get; } = [];

    public OverlayLayoutSettings Layout { get; } = new();

    public SectionSettings[] SectionSettings { get; } =
    [
        new(2, 5, 16),
        new(2, 5, 2)
    ];

    public QuickslotSection? SelectedSection { get; set; }

    public int CurrentSectionIndex { get; set; }

    public int NextSectionId { get; set; } = 1;
}

public sealed class OverlayLayoutSettings
{
    public double CanvasWidth { get; set; } = 720;

    public double CanvasHeight { get; set; } = 320;

    public double ScreenLeft { get; set; } = 120;

    public double ScreenTop { get; set; } = 120;

    public double Opacity { get; set; } = 1;

    public string StopHotkey { get; set; } = "Ctrl+Shift+F8";

    public int RefreshFps { get; set; } = 30;

    public double SlotScale { get; set; } = 1.5;

    public double GridSnapSize { get; set; } = 10;
}
