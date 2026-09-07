using System.Windows;
using TestOverlay.App.Models;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayWorkspaceStateTests
{
    [Fact]
    public void NewWorkspace_ProvidesSupportedLayoutAndSectionDefaults()
    {
        var workspace = new OverlayWorkspaceState();

        Assert.Equal(720, workspace.Layout.CanvasWidth);
        Assert.Equal(320, workspace.Layout.CanvasHeight);
        Assert.Equal("Ctrl+Shift+F8", workspace.Layout.StopHotkey);
        Assert.Equal(2, workspace.SectionSettings.Length);
        Assert.Equal(1, workspace.NextSectionId);
        Assert.Empty(workspace.Candidates);
        Assert.Empty(workspace.Sections);
        Assert.Empty(workspace.OverlaySlots);
    }

    [Fact]
    public void QuickslotSection_LabelReflectsIdentityPatternAndCandidateCount()
    {
        var seed = new SlotCandidate(1, new Rect(0, 0, 32, 32), 1);
        var section = new QuickslotSection(
            4,
            seed,
            1,
            new SectionSettings(2, 5, 2),
            [seed]);

        Assert.Equal("#04 vertical 2x8 (1)", section.Label);
    }
}
