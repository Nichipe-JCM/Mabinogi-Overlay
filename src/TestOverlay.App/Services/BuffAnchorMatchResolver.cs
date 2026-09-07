using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class BuffAnchorMatchResolver
{
    public static string AnchorKey(BuffIconMatch match) =>
        string.IsNullOrWhiteSpace(match.TemplateId)
            ? match.NameKey
            : $"{match.NameKey}|{match.TemplateId}";

    public static BuffAnchorObservationState ClassifyLogicalState(
        IReadOnlyCollection<BuffIconMatch> expected,
        IReadOnlyCollection<BuffIconMatch> observed)
    {
        if (expected.Count == 0 || observed.Count == 0)
        {
            return BuffAnchorObservationState.Indeterminate;
        }

        var states = observed
            .Select(BuffAnchorObservationClassifier.Classify)
            .ToArray();
        if (states.Contains(BuffAnchorObservationState.Active))
        {
            return BuffAnchorObservationState.Active;
        }

        if (observed.Count < expected.Count ||
            states.Any(state => state != BuffAnchorObservationState.Inactive))
        {
            return BuffAnchorObservationState.Indeterminate;
        }

        return BuffAnchorObservationState.Inactive;
    }

    public static BuffIconMatch? SelectActiveMatch(IEnumerable<BuffIconMatch> observed) =>
        observed
            .Where(match => BuffAnchorObservationClassifier.Classify(match) == BuffAnchorObservationState.Active)
            .OrderByDescending(match => match.StateConfidence)
            .ThenByDescending(match => match.StructureScore)
            .FirstOrDefault();
}
