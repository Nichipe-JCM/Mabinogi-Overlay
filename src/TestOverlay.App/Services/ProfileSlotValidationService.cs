using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ProfileSlotValidationService(
    RoiSectionDetectionService sectionDetection,
    MonitorTemplateDetectionService monitorDetection)
{
    private const double PositionTolerance = 3;

    public ProfileSlotValidationReport Validate(OverlayProfile profile, BitmapSource source)
    {
        var items = new List<ProfileSlotValidationItem>();
        ValidateQuickslotSections(profile, source, items);
        ValidateUnsectionedQuickslots(profile, source, items);
        ValidateBuffWindow(profile, source, items);
        ValidateTuairim(profile, source, items);
        if (items.Count == 0)
        {
            items.Add(new ProfileSlotValidationItem(
                ProfileSlotValidationState.Warning,
                "profile.validation.none.title",
                "profile.validation.none.detail"));
        }

        return new ProfileSlotValidationReport(items);
    }

    private void ValidateQuickslotSections(
        OverlayProfile profile,
        BitmapSource source,
        ICollection<ProfileSlotValidationItem> items)
    {
        var candidates = profile.Candidates.ToDictionary(candidate => candidate.Id);
        foreach (var section in profile.Sections)
        {
            var expected = section.CandidateIds
                .Select(id => candidates.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate?.Kind == OverlayElementKind.Quickslot)
                .Cast<OverlayProfileCandidate>()
                .Select(CandidateRect)
                .ToArray();
            if (expected.Length == 0)
            {
                continue;
            }

            var searchRoi = Union(expected);
            searchRoi.Inflate(
                Math.Max(24, expected.Average(rect => rect.Width)),
                Math.Max(24, expected.Average(rect => rect.Height)));
            var pattern = section.PatternIndex == 1
                ? QuickslotSectionPatternKind.Vertical
                : QuickslotSectionPatternKind.TopGrouped;
            var detected = sectionDetection.Detect(source, searchRoi, pattern);
            var titleArgs = new object?[] { section.Id };
            if (detected is null || detected.Slots.Count != expected.Length)
            {
                items.Add(new ProfileSlotValidationItem(
                    ProfileSlotValidationState.Invalid,
                    "profile.validation.section.title",
                    "profile.validation.section.not.found",
                    titleArgs));
                continue;
            }

            var expectedCenter = CenterOf(expected);
            var detectedCenter = CenterOf(detected.Slots);
            var offsetX = detectedCenter.X - expectedCenter.X;
            var offsetY = detectedCenter.Y - expectedCenter.Y;
            var averageSizeDifference = detected.Slots
                .Zip(expected)
                .Average(pair =>
                    Math.Abs(pair.First.Width - pair.Second.Width) +
                    Math.Abs(pair.First.Height - pair.Second.Height));
            var moved = Math.Abs(offsetX) > PositionTolerance ||
                        Math.Abs(offsetY) > PositionTolerance ||
                        averageSizeDifference > PositionTolerance * 2;
            items.Add(new ProfileSlotValidationItem(
                moved ? ProfileSlotValidationState.Invalid : ProfileSlotValidationState.Valid,
                "profile.validation.section.title",
                moved ? "profile.validation.section.moved" : "profile.validation.section.valid",
                titleArgs,
                expected.Length,
                offsetX.ToString("+0.0;-0.0;0.0"),
                offsetY.ToString("+0.0;-0.0;0.0")));
        }
    }

    private static void ValidateUnsectionedQuickslots(
        OverlayProfile profile,
        BitmapSource source,
        ICollection<ProfileSlotValidationItem> items)
    {
        var sectionCandidateIds = profile.Sections.SelectMany(section => section.CandidateIds).ToHashSet();
        var manual = profile.Candidates
            .Where(candidate => candidate.Kind == OverlayElementKind.Quickslot && !sectionCandidateIds.Contains(candidate.Id))
            .ToArray();
        if (manual.Length == 0)
        {
            return;
        }

        var outside = manual.Count(candidate => !InsideImage(CandidateRect(candidate), source));
        items.Add(new ProfileSlotValidationItem(
            outside > 0 ? ProfileSlotValidationState.Invalid : ProfileSlotValidationState.Warning,
            "profile.validation.manual.title",
            outside > 0 ? "profile.validation.manual.outside" : "profile.validation.manual.range.only",
            [],
            manual.Length,
            outside));
    }

    private void ValidateBuffWindow(
        OverlayProfile profile,
        BitmapSource source,
        ICollection<ProfileSlotValidationItem> items)
    {
        if (profile.BuffMonitorRoi is null || profile.BuffAnchors.Count == 0)
        {
            return;
        }

        var roi = ProfileRect(profile.BuffMonitorRoi);
        var detected = monitorDetection.DetectBuffs(source, roi).Matches;
        var pairs = profile.BuffAnchors
            .Select(saved => (Saved: saved, Current: detected.FirstOrDefault(match => match.NameKey == saved.NameKey)))
            .Where(pair => pair.Current is not null)
            .ToArray();
        var required = Math.Max(1, (int)Math.Ceiling(profile.BuffAnchors.Count * 0.5));
        if (pairs.Length < required)
        {
            items.Add(new ProfileSlotValidationItem(
                ProfileSlotValidationState.Invalid,
                "profile.validation.buff.title",
                "profile.validation.buff.not.found",
                [],
                pairs.Length,
                profile.BuffAnchors.Count));
            return;
        }

        var offsetX = pairs.Average(pair => Center(pair.Current!.Bounds).X - Center(ProfileRect(pair.Saved.Bounds)).X);
        var offsetY = pairs.Average(pair => Center(pair.Current!.Bounds).Y - Center(ProfileRect(pair.Saved.Bounds)).Y);
        var moved = Math.Abs(offsetX) > PositionTolerance || Math.Abs(offsetY) > PositionTolerance;
        items.Add(new ProfileSlotValidationItem(
            moved ? ProfileSlotValidationState.Invalid : ProfileSlotValidationState.Valid,
            "profile.validation.buff.title",
            moved ? "profile.validation.buff.moved" : "profile.validation.buff.valid",
            [],
            pairs.Length,
            profile.BuffAnchors.Count,
            offsetX.ToString("+0.0;-0.0;0.0"),
            offsetY.ToString("+0.0;-0.0;0.0")));
    }

    private void ValidateTuairim(
        OverlayProfile profile,
        BitmapSource source,
        ICollection<ProfileSlotValidationItem> items)
    {
        if (profile.TuairimMonitorRoi is null || profile.TuairimAnchor is null)
        {
            return;
        }

        var detected = monitorDetection.DetectTuairim(source, ProfileRect(profile.TuairimMonitorRoi));
        if (detected is null)
        {
            items.Add(new ProfileSlotValidationItem(
                ProfileSlotValidationState.Invalid,
                "profile.validation.tuairim.title",
                "profile.validation.tuairim.not.found"));
            return;
        }

        var savedCenter = Center(ProfileRect(profile.TuairimAnchor));
        var currentCenter = Center(detected.Bounds);
        var offsetX = currentCenter.X - savedCenter.X;
        var offsetY = currentCenter.Y - savedCenter.Y;
        var moved = Math.Abs(offsetX) > PositionTolerance || Math.Abs(offsetY) > PositionTolerance;
        items.Add(new ProfileSlotValidationItem(
            moved ? ProfileSlotValidationState.Invalid : ProfileSlotValidationState.Valid,
            "profile.validation.tuairim.title",
            moved ? "profile.validation.tuairim.moved" : "profile.validation.tuairim.valid",
            [],
            offsetX.ToString("+0.0;-0.0;0.0"),
            offsetY.ToString("+0.0;-0.0;0.0")));
    }

    private static Rect CandidateRect(OverlayProfileCandidate candidate) =>
        new(candidate.SourceX, candidate.SourceY, candidate.SourceWidth, candidate.SourceHeight);

    private static Rect ProfileRect(OverlayProfileRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private static bool InsideImage(Rect rect, BitmapSource source) =>
        rect.X >= 0 && rect.Y >= 0 && rect.Right <= source.PixelWidth && rect.Bottom <= source.PixelHeight;

    private static Rect Union(IReadOnlyList<Rect> rects)
    {
        var result = rects[0];
        foreach (var rect in rects.Skip(1))
        {
            result.Union(rect);
        }
        return result;
    }

    private static Point CenterOf(IReadOnlyList<Rect> rects) =>
        new(rects.Average(rect => Center(rect).X), rects.Average(rect => Center(rect).Y));

    private static Point Center(Rect rect) =>
        new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
}

public enum ProfileSlotValidationState
{
    Valid,
    Warning,
    Invalid
}

public sealed record ProfileSlotValidationItem(
    ProfileSlotValidationState State,
    string TitleKey,
    string DetailKey,
    object?[]? TitleArguments = null,
    params object?[] DetailArguments);

public sealed record ProfileSlotValidationReport(IReadOnlyList<ProfileSlotValidationItem> Items)
{
    public int ValidCount => Items.Count(item => item.State == ProfileSlotValidationState.Valid);
    public int WarningCount => Items.Count(item => item.State == ProfileSlotValidationState.Warning);
    public int InvalidCount => Items.Count(item => item.State == ProfileSlotValidationState.Invalid);
}
