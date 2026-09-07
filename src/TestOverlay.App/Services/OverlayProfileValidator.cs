using System.IO;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

internal static class OverlayProfileValidator
{
    internal const double MaximumCanvasDimension = 16384;
    internal const double MaximumCanvasArea = 67_108_864;
    internal const double MaximumRectDimension = 32768;
    internal const double MaximumCoordinateMagnitude = 1_000_000;
    internal const int MaximumCandidates = 4096;
    internal const int MaximumSlots = 4096;
    internal const int MaximumSections = 1024;
    internal const int MaximumAnchors = 128;
    internal const int MaximumCustomTimers = 64;

    public static void Validate(OverlayProfile profile)
    {
        RequireInRange(profile.CanvasWidth, 1, MaximumCanvasDimension, nameof(profile.CanvasWidth));
        RequireInRange(profile.CanvasHeight, 1, MaximumCanvasDimension, nameof(profile.CanvasHeight));
        if (profile.CanvasWidth * profile.CanvasHeight > MaximumCanvasArea)
        {
            throw Invalid($"Canvas area exceeds the supported maximum of {MaximumCanvasArea}.");
        }
        RequireInRange(profile.ScreenLeft, -MaximumCoordinateMagnitude, MaximumCoordinateMagnitude, nameof(profile.ScreenLeft));
        RequireInRange(profile.ScreenTop, -MaximumCoordinateMagnitude, MaximumCoordinateMagnitude, nameof(profile.ScreenTop));
        RequireInRange(profile.Opacity, 0, 1, nameof(profile.Opacity));
        RequireInRange(profile.LayoutSlotScale, 0.1, 10, nameof(profile.LayoutSlotScale));
        RequireInRange(profile.GridSnapSize, 1, 512, nameof(profile.GridSnapSize));
        if (profile.AlertPreviewRows is < 1 or > 4)
        {
            throw Invalid("AlertPreviewRows must be between 1 and 4.");
        }

        RequireCollection(profile.Candidates, nameof(profile.Candidates));
        RequireCollection(profile.Sections, nameof(profile.Sections));
        RequireCollection(profile.Slots, nameof(profile.Slots));
        RequireCollection(profile.SectionSettings, nameof(profile.SectionSettings));
        RequireCollection(profile.RecognizedBuffNameKeys, nameof(profile.RecognizedBuffNameKeys));
        RequireCollection(profile.SelectedBuffNameKeys, nameof(profile.SelectedBuffNameKeys));
        RequireCollection(profile.BuffAnchors, nameof(profile.BuffAnchors));
        RequireCollection(profile.BuffAlertSoundPaths, nameof(profile.BuffAlertSoundPaths));
        RequireCollection(profile.BuffAlertVolumes, nameof(profile.BuffAlertVolumes));
        RequireCollection(profile.CustomTimers, nameof(profile.CustomTimers));
        RequireMaximumCount(profile.Candidates, MaximumCandidates, nameof(profile.Candidates));
        RequireMaximumCount(profile.Sections, MaximumSections, nameof(profile.Sections));
        RequireMaximumCount(profile.Slots, MaximumSlots, nameof(profile.Slots));
        RequireMaximumCount(profile.BuffAnchors, MaximumAnchors, nameof(profile.BuffAnchors));
        RequireMaximumCount(profile.CustomTimers, MaximumCustomTimers, nameof(profile.CustomTimers));

        ValidateRect(profile.BuffMonitorRoi, nameof(profile.BuffMonitorRoi));
        ValidateRect(profile.TuairimMonitorRoi, nameof(profile.TuairimMonitorRoi));
        ValidateRect(profile.TuairimAnchor, nameof(profile.TuairimAnchor));

        var candidateIds = new HashSet<int>();
        foreach (var candidate in profile.Candidates)
        {
            if (!candidateIds.Add(candidate.Id))
            {
                throw Invalid($"Candidate IDs must be unique: {candidate.Id}.");
            }

            if (!Enum.IsDefined(candidate.Kind))
            {
                throw Invalid($"Candidate {candidate.Id} has an unknown element kind: {candidate.Kind}.");
            }

            if (candidate.Kind == OverlayElementKind.Quickslot)
            {
                if (candidate.Id <= 0 || candidate.IsBuiltIn)
                {
                    throw Invalid($"Quickslot candidate IDs must be positive and cannot be built-in: {candidate.Id}.");
                }
            }
            else
            {
                var reservedId = BuiltInOverlayElementIds.For(candidate.Kind);
                if (!candidate.IsBuiltIn || candidate.Id != reservedId)
                {
                    throw Invalid(
                        $"Built-in candidate {candidate.Kind} must use reserved ID {reservedId}: {candidate.Id}.");
                }
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
            RequireInRange(slot.Opacity, 0, 1, "Slot.Opacity");
            RequireInRange(slot.Scale, 0.1, 10, "Slot.Scale");
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

        var customTimerIds = new HashSet<int>();
        foreach (var timer in profile.CustomTimers)
        {
            if (timer.Id <= 0 || !customTimerIds.Add(timer.Id))
            {
                throw Invalid($"Custom timer IDs must be positive and unique: {timer.Id}.");
            }
            if (string.IsNullOrWhiteSpace(timer.Name))
            {
                throw Invalid($"Custom timer {timer.Id} has no name.");
            }
            if (timer.DurationSeconds is < 1 or > 86400 ||
                timer.AlertBeforeSeconds < 0 ||
                timer.AlertBeforeSeconds > timer.DurationSeconds ||
                timer.Volume is < 0 or > 100)
            {
                throw Invalid($"Custom timer {timer.Id} has values outside the supported range.");
            }
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
        RequireInRange(x, -MaximumCoordinateMagnitude, MaximumCoordinateMagnitude, $"{name}.X");
        RequireInRange(y, -MaximumCoordinateMagnitude, MaximumCoordinateMagnitude, $"{name}.Y");
        RequireInRange(width, 1, MaximumRectDimension, $"{name}.Width");
        RequireInRange(height, 1, MaximumRectDimension, $"{name}.Height");
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw Invalid($"{name} must be finite.");
        }
    }

    private static void RequireInRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw Invalid($"{name} must be between {minimum} and {maximum}.");
        }
    }

    private static void RequireCollection<T>(ICollection<T>? collection, string name)
    {
        if (collection is null || collection.Any(item => item is null))
        {
            throw Invalid($"{name} is missing.");
        }
    }

    private static void RequireMaximumCount<T>(ICollection<T> collection, int maximum, string name)
    {
        if (collection.Count > maximum)
        {
            throw Invalid($"{name} exceeds the supported maximum count of {maximum}.");
        }
    }

    private static InvalidDataException Invalid(string message) =>
        new($"Invalid overlay profile: {message}");
}
