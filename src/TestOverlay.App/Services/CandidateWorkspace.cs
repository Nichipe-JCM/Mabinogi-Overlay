using System.Windows;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class CandidateWorkspace
{
    private readonly OverlayWorkspaceState _workspace;
    private readonly Stack<CandidateEditSnapshot> _undoStack = new();
    private readonly Stack<CandidateEditSnapshot> _redoStack = new();

    public CandidateWorkspace(OverlayWorkspaceState workspace)
    {
        _workspace = workspace;
    }

    public CandidateEditSnapshot CaptureSnapshot(int selectedCandidateId)
    {
        return new CandidateEditSnapshot(
            _workspace.Candidates
                .Select(candidate => new CandidateState(
                    candidate.Id,
                    candidate.SourceRect.X,
                    candidate.SourceRect.Y,
                    candidate.SourceRect.Width,
                    candidate.SourceRect.Height,
                    candidate.Score,
                    candidate.IsSelected,
                    candidate.Kind,
                    candidate.DisplayNameKey,
                    candidate.IsBuiltIn))
                .ToList(),
            _workspace.Sections
                .Select(section => new SectionState(
                    section.Id,
                    section.Seed.Id,
                    section.PatternIndex,
                    section.Settings.SmallGapX,
                    section.Settings.SmallGapY,
                    section.Settings.LargeGap,
                    section.Candidates.Select(candidate => candidate.Id).ToList()))
                .ToList(),
            _workspace.SelectedSection?.Id ?? 0,
            _workspace.NextSectionId,
            selectedCandidateId);
    }

    public bool PushUndoIfChanged(CandidateEditSnapshot before, int selectedCandidateId)
    {
        if (SnapshotsEqual(before, CaptureSnapshot(selectedCandidateId)))
        {
            return false;
        }

        _undoStack.Push(before);
        _redoStack.Clear();
        return true;
    }

    public bool TryUndo(int selectedCandidateId, out CandidateEditSnapshot snapshot)
    {
        if (_undoStack.Count == 0)
        {
            snapshot = default!;
            return false;
        }

        _redoStack.Push(CaptureSnapshot(selectedCandidateId));
        snapshot = _undoStack.Pop();
        return true;
    }

    public bool TryRedo(int selectedCandidateId, out CandidateEditSnapshot snapshot)
    {
        if (_redoStack.Count == 0)
        {
            snapshot = default!;
            return false;
        }

        _undoStack.Push(CaptureSnapshot(selectedCandidateId));
        snapshot = _redoStack.Pop();
        return true;
    }

    public CandidateRestoreResult RestoreSnapshot(
        CandidateEditSnapshot snapshot,
        Func<OverlayElementKind, bool> includeKind)
    {
        _workspace.Candidates.Clear();
        _workspace.Sections.Clear();
        _workspace.SelectedSection = null;

        var restoredById = new Dictionary<int, SlotCandidate>();
        SlotCandidate? selectedCandidate = null;
        foreach (var saved in snapshot.Candidates.Where(candidate => includeKind(candidate.Kind)))
        {
            var candidate = new SlotCandidate(
                saved.Id,
                new Rect(saved.X, saved.Y, saved.Width, saved.Height),
                saved.Score,
                saved.Kind,
                saved.DisplayNameKey,
                saved.IsBuiltIn)
            {
                IsSelected = saved.IsSelected
            };
            _workspace.Candidates.Add(candidate);
            restoredById[candidate.Id] = candidate;
            if (candidate.Id == snapshot.SelectedId)
            {
                selectedCandidate = candidate;
            }
        }

        QuickslotSection? selectedSection = null;
        foreach (var savedSection in snapshot.Sections)
        {
            if (!restoredById.TryGetValue(savedSection.SeedId, out var seed))
            {
                continue;
            }

            var candidates = savedSection.CandidateIds
                .Select(id => restoredById.GetValueOrDefault(id))
                .Where(candidate => candidate is not null)
                .Cast<SlotCandidate>()
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var section = new QuickslotSection(
                savedSection.Id,
                seed,
                savedSection.PatternIndex,
                new SectionSettings(savedSection.SmallGapX, savedSection.SmallGapY, savedSection.LargeGap),
                candidates);
            _workspace.Sections.Add(section);
            if (section.Id == snapshot.SelectedSectionId)
            {
                selectedSection = section;
            }
        }

        _workspace.SelectedSection = selectedSection;
        _workspace.NextSectionId = Math.Max(
            snapshot.NextSectionId,
            _workspace.Sections.Count == 0 ? 1 : _workspace.Sections.Max(section => section.Id) + 1);
        RefreshSectionMemberships();
        return new CandidateRestoreResult(restoredById, selectedCandidate, selectedSection);
    }

    public int DeleteCandidates(IReadOnlyCollection<SlotCandidate> candidates)
    {
        var removable = candidates.Where(candidate => !candidate.IsBuiltIn).ToHashSet();
        foreach (var candidate in removable)
        {
            _workspace.Candidates.Remove(candidate);
        }

        RemoveOverlaySlotsForCandidates(removable);
        RemoveSectionsContaining(removable);
        UpdateCandidateOverlayFlags();
        return removable.Count;
    }

    public int RemoveOverlaySlotsForCandidates(IEnumerable<SlotCandidate> candidates)
    {
        var candidateSet = candidates.ToHashSet();
        var candidateIds = candidateSet.Select(candidate => candidate.Id).ToHashSet();
        return _workspace.OverlaySlots.RemoveAll(slot =>
            candidateSet.Contains(slot.Source) || candidateIds.Contains(slot.Source.Id));
    }

    public void UpdateCandidateOverlayFlags()
    {
        var overlayCandidateIds = _workspace.OverlaySlots.Select(slot => slot.Source.Id).ToHashSet();
        foreach (var candidate in _workspace.Candidates)
        {
            candidate.IsInOverlay = overlayCandidateIds.Contains(candidate.Id);
        }
    }

    public bool RemoveSectionsContaining(IReadOnlyCollection<SlotCandidate> candidates)
    {
        var removed = _workspace.Sections
            .Where(section => section.Candidates.Any(candidates.Contains))
            .ToList();
        foreach (var section in removed)
        {
            _workspace.Sections.Remove(section);
        }

        var selectedRemoved = _workspace.SelectedSection is not null &&
                              removed.Contains(_workspace.SelectedSection);
        if (selectedRemoved)
        {
            _workspace.SelectedSection = null;
        }

        RefreshSectionMemberships();
        return selectedRemoved;
    }

    public void ClearSections()
    {
        _workspace.Sections.Clear();
        _workspace.SelectedSection = null;
        _workspace.NextSectionId = 1;
        RefreshSectionMemberships();
    }

    public void RefreshSectionMemberships()
    {
        foreach (var candidate in _workspace.Candidates)
        {
            candidate.SectionMembership = string.Empty;
        }

        foreach (var section in _workspace.Sections)
        {
            foreach (var candidate in section.Candidates.Where(_workspace.Candidates.Contains))
            {
                candidate.SectionMembership = string.IsNullOrWhiteSpace(candidate.SectionMembership)
                    ? $"section {section.Id:00}"
                    : $"{candidate.SectionMembership},{section.Id:00}";
            }
        }
    }

    public int NextCandidateId() =>
        _workspace.Candidates
            .Where(candidate => candidate.Id > 0)
            .Select(candidate => candidate.Id)
            .DefaultIfEmpty(0)
            .Max() + 1;

    public void MoveCandidate(
        SlotCandidate candidate,
        double x,
        double y,
        double canvasWidth,
        double canvasHeight,
        double border)
    {
        var clampedX = Math.Clamp(
            x,
            border,
            Math.Max(border, canvasWidth - candidate.SourceRect.Width - border));
        var clampedY = Math.Clamp(
            y,
            border,
            Math.Max(border, canvasHeight - candidate.SourceRect.Height - border));
        candidate.MoveTo(clampedX, clampedY);
    }

    public void SelectOnly(SlotCandidate selected)
    {
        foreach (var candidate in _workspace.Candidates)
        {
            candidate.IsSelected = ReferenceEquals(candidate, selected);
        }
    }

    public void ClearSelection()
    {
        foreach (var candidate in _workspace.Candidates)
        {
            candidate.IsSelected = false;
        }
    }

    public QuickslotSection AddSection(
        SlotCandidate seed,
        int patternIndex,
        SectionSettings settings,
        List<SlotCandidate> candidates)
    {
        var section = new QuickslotSection(
            _workspace.NextSectionId++,
            seed,
            patternIndex,
            settings,
            candidates);
        _workspace.Sections.Add(section);
        _workspace.SelectedSection = section;
        RefreshSectionMemberships();
        return section;
    }

    private static bool SnapshotsEqual(CandidateEditSnapshot left, CandidateEditSnapshot right)
    {
        if (left.SelectedId != right.SelectedId ||
            left.SelectedSectionId != right.SelectedSectionId ||
            left.NextSectionId != right.NextSectionId ||
            left.Candidates.Count != right.Candidates.Count ||
            left.Sections.Count != right.Sections.Count)
        {
            return false;
        }

        return left.Candidates.SequenceEqual(right.Candidates) &&
               left.Sections.Zip(right.Sections).All(pair => SectionStatesEqual(pair.First, pair.Second));
    }

    private static bool SectionStatesEqual(SectionState left, SectionState right) =>
        left.Id == right.Id &&
        left.SeedId == right.SeedId &&
        left.PatternIndex == right.PatternIndex &&
        left.SmallGapX.Equals(right.SmallGapX) &&
        left.SmallGapY.Equals(right.SmallGapY) &&
        left.LargeGap.Equals(right.LargeGap) &&
        left.CandidateIds.SequenceEqual(right.CandidateIds);
}

public sealed record CandidateRestoreResult(
    IReadOnlyDictionary<int, SlotCandidate> CandidatesById,
    SlotCandidate? SelectedCandidate,
    QuickslotSection? SelectedSection);
