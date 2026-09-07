namespace TestOverlay.App.Models;

public sealed class QuickslotSection
{
    public QuickslotSection(
        int id,
        SlotCandidate seed,
        int patternIndex,
        SectionSettings settings,
        List<SlotCandidate> candidates)
    {
        Id = id;
        Seed = seed;
        PatternIndex = patternIndex;
        Settings = settings;
        Candidates = candidates;
    }

    public int Id { get; }

    public SlotCandidate Seed { get; }

    public int PatternIndex { get; }

    public SectionSettings Settings { get; set; }

    public List<SlotCandidate> Candidates { get; }

    public string Label => $"#{Id:00} {SectionPattern.NameFor(PatternIndex)} ({Candidates.Count})";

    public void RefreshLabel()
    {
    }
}

public sealed record SectionSettings(double SmallGapX, double SmallGapY, double LargeGap);

public sealed record SectionPattern(
    string Name,
    int GroupColumns,
    int GroupRows,
    int GroupColumnsCount,
    int GroupRowsCount,
    Func<double, double> InnerGapX,
    Func<double, double> InnerGapY,
    Func<double, double> GroupGapX,
    Func<double, double> GroupGapY)
{
    public static string NameFor(int index) =>
        index == 1 ? Vertical().Name : TopGrouped().Name;

    public static SectionPattern TopGrouped() => new(
        "top grouped 4x2 x3",
        4,
        2,
        3,
        1,
        smallGap => smallGap,
        smallGap => smallGap,
        largeGap => largeGap,
        _ => 0);

    public static SectionPattern Vertical() => new(
        "vertical 2x8",
        2,
        8,
        1,
        1,
        smallGap => smallGap,
        smallGap => smallGap,
        _ => 0,
        _ => 0);
}
