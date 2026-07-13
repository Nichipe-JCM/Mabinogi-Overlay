using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayProfileMapperTests
{
    [Fact]
    public void CreateWorkspaceProfile_MapsCandidatesSectionsSlotsAndLayout()
    {
        var workspace = new OverlayWorkspaceState();
        workspace.Layout.CanvasWidth = 900;
        workspace.Layout.RefreshFps = 60;
        workspace.CurrentSectionIndex = 1;
        var candidate = new SlotCandidate(8, new Rect(10, 20, 32, 32), 95) { IsSelected = true };
        workspace.Candidates.Add(candidate);
        workspace.Sections.Add(new QuickslotSection(2, candidate, 1, new SectionSettings(2, 5, 2), [candidate]));
        workspace.OverlaySlots.Add(new OverlaySlot(
            candidate,
            new Rect(4, 6, 48, 48),
            CreatePixel(),
            0.75,
            1.5,
            true));

        var profile = OverlayProfileMapper.CreateWorkspaceProfile("mapped", workspace, 29, 31, 17);

        Assert.Equal("mapped", profile.Name);
        Assert.Equal(900, profile.CanvasWidth);
        Assert.Equal(60, profile.RefreshFps);
        Assert.Equal(1, profile.SelectedSectionPattern);
        Assert.Equal(8, Assert.Single(profile.Candidates).Id);
        Assert.Equal(2, Assert.Single(profile.Sections).Id);
        Assert.Equal(0.75, Assert.Single(profile.Slots).Opacity);
        Assert.Equal(29, profile.SlotInnerSize);
    }

    [Fact]
    public void ApplyLayoutAndSectionSettings_ClampsPersistedValues()
    {
        var workspace = new OverlayWorkspaceState();
        var profile = new OverlayProfile
        {
            CanvasWidth = 1,
            CanvasHeight = 1,
            Opacity = 5,
            LayoutSlotScale = 0,
            GridSnapSize = 100,
            SelectedSectionPattern = 99,
            SectionSettings =
            [
                new OverlayProfileSectionSettings
                {
                    PatternIndex = 0,
                    SmallGapX = -1,
                    SmallGapY = 200,
                    LargeGap = 1
                }
            ]
        };

        OverlayProfileMapper.ApplyLayoutAndSectionSettings(profile, workspace, 144);

        Assert.Equal(120, workspace.Layout.CanvasWidth);
        Assert.Equal(80, workspace.Layout.CanvasHeight);
        Assert.Equal(1, workspace.Layout.Opacity);
        Assert.Equal(0.1, workspace.Layout.SlotScale);
        Assert.Equal(64, workspace.Layout.GridSnapSize);
        Assert.Equal(1, workspace.CurrentSectionIndex);
        Assert.Equal(new SectionSettings(2, 30, 2), workspace.SectionSettings[0]);
    }

    private static BitmapSource CreatePixel() => BitmapSource.Create(
        1,
        1,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        new byte[4],
        4);
}
