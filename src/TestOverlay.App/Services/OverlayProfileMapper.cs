using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class OverlayProfileMapper
{
    public static OverlayProfile CreateWorkspaceProfile(
        string profileName,
        OverlayWorkspaceState workspace,
        int slotInnerWidth,
        int slotInnerHeight,
        int refreshIntervalMs)
    {
        var layout = workspace.Layout;
        return new OverlayProfile
        {
            Name = profileName,
            CanvasWidth = layout.CanvasWidth,
            CanvasHeight = layout.CanvasHeight,
            ScreenLeft = layout.ScreenLeft,
            ScreenTop = layout.ScreenTop,
            Opacity = layout.Opacity,
            StopHotkey = layout.StopHotkey,
            RefreshIntervalMs = refreshIntervalMs,
            RefreshFps = layout.RefreshFps,
            LayoutSlotScale = Math.Clamp(layout.SlotScale, 0.1, 10),
            GridSnapSize = layout.GridSnapSize,
            SlotInnerSize = Math.Min(slotInnerWidth, slotInnerHeight),
            SlotInnerWidth = slotInnerWidth,
            SlotInnerHeight = slotInnerHeight,
            SelectedSectionPattern = Math.Clamp(
                workspace.CurrentSectionIndex,
                0,
                workspace.SectionSettings.Length - 1),
            SectionSettings = workspace.SectionSettings
                .Select((settings, index) => new OverlayProfileSectionSettings
                {
                    PatternIndex = index,
                    PatternName = SectionPattern.NameFor(index),
                    SmallGapX = settings.SmallGapX,
                    SmallGapY = settings.SmallGapY,
                    LargeGap = settings.LargeGap
                })
                .ToList(),
            Candidates = workspace.Candidates.Select(candidate => new OverlayProfileCandidate
            {
                Id = candidate.Id,
                SourceX = candidate.SourceRect.X,
                SourceY = candidate.SourceRect.Y,
                SourceWidth = candidate.SourceRect.Width,
                SourceHeight = candidate.SourceRect.Height,
                Score = candidate.Score,
                IsSelected = candidate.IsSelected,
                Kind = candidate.Kind,
                DisplayNameKey = candidate.DisplayNameKey,
                IsBuiltIn = candidate.IsBuiltIn
            }).ToList(),
            Sections = workspace.Sections.Select(section => new OverlayProfileSection
            {
                Id = section.Id,
                SeedCandidateId = section.Seed.Id,
                PatternIndex = section.PatternIndex,
                SmallGapX = section.Settings.SmallGapX,
                SmallGapY = section.Settings.SmallGapY,
                LargeGap = section.Settings.LargeGap,
                CandidateIds = section.Candidates.Select(candidate => candidate.Id).ToList()
            }).ToList(),
            Slots = workspace.OverlaySlots.Select(slot => new OverlayProfileSlot
            {
                SourceCandidateId = slot.Source.Id,
                SourceX = slot.Source.SourceRect.X,
                SourceY = slot.Source.SourceRect.Y,
                SourceWidth = slot.Source.SourceRect.Width,
                SourceHeight = slot.Source.SourceRect.Height,
                OverlayX = slot.OverlayRect.X,
                OverlayY = slot.OverlayRect.Y,
                OverlayWidth = slot.OverlayRect.Width,
                OverlayHeight = slot.OverlayRect.Height,
                Opacity = slot.Opacity,
                HasOpacityOverride = slot.HasOpacityOverride,
                Scale = slot.Scale
            }).ToList()
        };
    }

    public static void ApplyLayoutAndSectionSettings(
        OverlayProfile profile,
        OverlayWorkspaceState workspace,
        int refreshFps)
    {
        workspace.Layout.CanvasWidth = Math.Max(120, profile.CanvasWidth);
        workspace.Layout.CanvasHeight = Math.Max(80, profile.CanvasHeight);
        workspace.Layout.ScreenLeft = profile.ScreenLeft;
        workspace.Layout.ScreenTop = profile.ScreenTop;
        workspace.Layout.Opacity = Math.Clamp(profile.Opacity, 0, 1);
        workspace.Layout.StopHotkey = profile.StopHotkey;
        workspace.Layout.RefreshFps = refreshFps;
        workspace.Layout.SlotScale = Math.Clamp(profile.LayoutSlotScale, 0.1, 10);
        workspace.Layout.GridSnapSize = Math.Clamp(profile.GridSnapSize > 0 ? profile.GridSnapSize : 10, 1, 64);

        foreach (var saved in profile.SectionSettings)
        {
            if (saved.PatternIndex < 0 || saved.PatternIndex >= workspace.SectionSettings.Length)
            {
                continue;
            }

            workspace.SectionSettings[saved.PatternIndex] = new SectionSettings(
                Math.Clamp(saved.SmallGapX, 2, 30),
                Math.Clamp(saved.SmallGapY, 2, 30),
                Math.Clamp(saved.LargeGap, 2, 60));
        }

        workspace.CurrentSectionIndex = Math.Clamp(
            profile.SelectedSectionPattern,
            0,
            workspace.SectionSettings.Length - 1);
    }
}
