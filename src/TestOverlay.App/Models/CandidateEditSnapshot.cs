namespace TestOverlay.App.Models;

public sealed record CandidateEditSnapshot(
    List<CandidateState> Candidates,
    List<SectionState> Sections,
    int SelectedSectionId,
    int NextSectionId,
    int SelectedId);

public sealed record CandidateState(
    int Id,
    double X,
    double Y,
    double Width,
    double Height,
    double Score,
    bool IsSelected,
    OverlayElementKind Kind,
    string? DisplayNameKey,
    bool IsBuiltIn);

public sealed record SectionState(
    int Id,
    int SeedId,
    int PatternIndex,
    double SmallGapX,
    double SmallGapY,
    double LargeGap,
    List<int> CandidateIds);
