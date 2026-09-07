using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class CandidateWorkspaceTests
{
    [Fact]
    public void DeleteCandidates_RemovesDependentSectionsAndOverlaySlots()
    {
        var state = new OverlayWorkspaceState();
        var first = Candidate(1, 0);
        var second = Candidate(2, 40);
        state.Candidates.Add(first);
        state.Candidates.Add(second);
        state.Sections.Add(new QuickslotSection(1, first, 0, new SectionSettings(2, 5, 16), [first, second]));
        state.SelectedSection = state.Sections[0];
        state.OverlaySlots.Add(new OverlaySlot(first, new Rect(0, 0, 48, 48), Pixel()));
        var workspace = new CandidateWorkspace(state);

        var removed = workspace.DeleteCandidates([first]);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(first, state.Candidates);
        Assert.Empty(state.Sections);
        Assert.Empty(state.OverlaySlots);
        Assert.Null(state.SelectedSection);
    }

    [Fact]
    public void UndoAndRedo_RestoreCandidateGeometryAndSelection()
    {
        var state = new OverlayWorkspaceState();
        var candidate = Candidate(1, 0);
        candidate.IsSelected = true;
        state.Candidates.Add(candidate);
        var workspace = new CandidateWorkspace(state);
        var before = workspace.CaptureSnapshot(candidate.Id);
        candidate.MoveTo(80, 20);
        Assert.True(workspace.PushUndoIfChanged(before, candidate.Id));

        Assert.True(workspace.TryUndo(candidate.Id, out var undo));
        var restoredUndo = workspace.RestoreSnapshot(undo, _ => true);
        Assert.Equal(new Rect(0, 0, 32, 32), restoredUndo.SelectedCandidate!.SourceRect);

        Assert.True(workspace.TryRedo(restoredUndo.SelectedCandidate.Id, out var redo));
        var restoredRedo = workspace.RestoreSnapshot(redo, _ => true);
        Assert.Equal(new Rect(80, 20, 32, 32), restoredRedo.SelectedCandidate!.SourceRect);
    }

    [Fact]
    public void NextCandidateId_IgnoresReservedNegativeIds()
    {
        var state = new OverlayWorkspaceState();
        state.Candidates.Add(new SlotCandidate(-100, new Rect(0, 0, 1, 1), 1));
        state.Candidates.Add(Candidate(7, 0));

        Assert.Equal(8, new CandidateWorkspace(state).NextCandidateId());
    }

    [Fact]
    public void MoveCandidate_ClampsGeometryInsideCanvasBorder()
    {
        var state = new OverlayWorkspaceState();
        var candidate = Candidate(1, 0);
        state.Candidates.Add(candidate);
        var workspace = new CandidateWorkspace(state);

        workspace.MoveCandidate(candidate, 500, -20, 100, 80, 1);

        Assert.Equal(new Rect(67, 1, 32, 32), candidate.SourceRect);
    }

    private static SlotCandidate Candidate(int id, double x) =>
        new(id, new Rect(x, 0, 32, 32), 90);

    private static BitmapSource Pixel() => BitmapSource.Create(
        1,
        1,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        new byte[4],
        4);
}
