using System.IO;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

internal static class OverlayProfileValidator
{
    public static void Validate(OverlayProfile profile)
    {
        RequireFinitePositive(profile.CanvasWidth, nameof(profile.CanvasWidth));
        RequireFinitePositive(profile.CanvasHeight, nameof(profile.CanvasHeight));
        RequireFinite(profile.ScreenLeft, nameof(profile.ScreenLeft));
        RequireFinite(profile.ScreenTop, nameof(profile.ScreenTop));
        RequireFinite(profile.Opacity, nameof(profile.Opacity));
        RequireFinitePositive(profile.LayoutSlotScale, nameof(profile.LayoutSlotScale));
        RequireFinitePositive(profile.GridSnapSize, nameof(profile.GridSnapSize));

        RequireCollection(profile.Candidates, nameof(profile.Candidates));
        RequireCollection(profile.Sections, nameof(profile.Sections));
        RequireCollection(profile.Slots, nameof(profile.Slots));
        RequireCollection(profile.SectionSettings, nameof(profile.SectionSettings));
        RequireCollection(profile.RecognizedBuffNameKeys, nameof(profile.RecognizedBuffNameKeys));
        RequireCollection(profile.SelectedBuffNameKeys, nameof(profile.SelectedBuffNameKeys));
        RequireCollection(profile.BuffAnchors, nameof(profile.BuffAnchors));
        RequireCollection(profile.BuffAlertSoundPaths, nameof(profile.BuffAlertSoundPaths));
        RequireCollection(profile.BuffAlertVolumes, nameof(profile.BuffAlertVolumes));

        ValidateRect(profile.BuffMonitorRoi, nameof(profile.BuffMonitorRoi));
        ValidateRect(profile.TuairimMonitorRoi, nameof(profile.TuairimMonitorRoi));
        ValidateRect(profile.TuairimAnchor, nameof(profile.TuairimAnchor));

        var candidateIds = new HashSet<int>();
        foreach (var candidate in profile.Candidates)
        {
            if (candidate.Id <= 0 || !candidateIds.Add(candidate.Id))
            {
                throw Invalid($"Candidate IDs must be positive and unique: {candidate.Id}.");
            }

            if (!Enum.IsDefined(candidate.Kind))
            {
                throw Invalid($"Candidate {candidate.Id} has an unknown element kind: {candidate.Kind}.");
            }

            ValidateRect(
                candidate.SourceX,
                candidate.SourceY,
                candidate.SourceWidth,
                candidate.SourceHeight,
                $"Candidate {candidate.Id}");
            RequireFinite(candidate.Score, $"Candidate {candidate.Id}.Score");
        }

        var sectionIds = new HashSet<int>();
        foreach (var section in profile.Sections)
        {
            if (section.Id <= 0 || !sectionIds.Add(section.Id))
            {
                throw Invalid($"Section IDs must be positive and unique: {section.Id}.");
            }

            if (!candidateIds.Contains(section.SeedCandidateId))
            {
                throw Invalid($"Section {section.Id} refers to missing seed candidate {section.SeedCandidateId}.");
            }

            RequireCollection(section.CandidateIds, $"Section {section.Id}.CandidateIds");
            if (section.CandidateIds.Count == 0 || section.CandidateIds.Any(id => !candidateIds.Contains(id)))
            {
                throw Invalid($"Section {section.Id} contains a missing candidate reference.");
            }

            RequireFinite(section.SmallGapX, $"Section {section.Id}.SmallGapX");
            RequireFinite(section.SmallGapY, $"Section {section.Id}.SmallGapY");
            RequireFinite(section.LargeGap, $"Section {section.Id}.LargeGap");
        }

        foreach (var slot in profile.Slots)
        {
            ValidateRect(slot.SourceX, slot.SourceY, slot.SourceWidth, slot.SourceHeight, "Slot source");
            ValidateRect(slot.OverlayX, slot.OverlayY, slot.OverlayWidth, slot.OverlayHeight, "Slot overlay");
            RequireFinite(slot.Opacity, "Slot.Opacity");
            RequireFinitePositive(slot.Scale, "Slot.Scale");
        }

        foreach (var anchor in profile.BuffAnchors)
        {
            if (string.IsNullOrWhiteSpace(anchor.NameKey))
            {
                throw Invalid("A buff anchor has no name key.");
            }

            ValidateRect(anchor.Bounds, $"Buff anchor {anchor.NameKey}");
            RequireFinite(anchor.StructureScore, $"Buff anchor {anchor.NameKey}.StructureScore");
            RequireFinite(anchor.StateConfidence, $"Buff anchor {anchor.NameKey}.StateConfidence");
        }
    }

    private static void ValidateRect(OverlayProfileRect? rect, string name)
    {
        if (rect is not null)
        {
            ValidateRect(rect.X, rect.Y, rect.Width, rect.Height, name);
        }
    }

    private static void ValidateRect(double x, double y, double width, double height, string name)
    {
        RequireFinite(x, $"{name}.X");
        RequireFinite(y, $"{name}.Y");
        RequireFinitePositive(width, $"{name}.Width");
        RequireFinitePositive(height, $"{name}.Height");
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw Invalid($"{name} must be finite.");
        }
    }

    private static void RequireFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw Invalid($"{name} must be a positive finite number.");
        }
    }

    private static void RequireCollection<T>(ICollection<T>? collection, string name)
    {
        if (collection is null)
        {
            throw Invalid($"{name} is missing.");
        }
    }

    private static InvalidDataException Invalid(string message) =>
        new($"Invalid overlay profile: {message}");
}
