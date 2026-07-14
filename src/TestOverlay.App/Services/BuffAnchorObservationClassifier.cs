using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public enum BuffAnchorObservationState
{
    Indeterminate,
    Active,
    Inactive
}

public static class BuffAnchorObservationClassifier
{
    public const double MinimumStateConfidence = 0.03;

    public static BuffAnchorObservationState Classify(BuffIconMatch? match)
    {
        if (match is null ||
            !double.IsFinite(match.StateConfidence) ||
            match.StateConfidence < MinimumStateConfidence)
        {
            return BuffAnchorObservationState.Indeterminate;
        }

        return match.IsActive
            ? BuffAnchorObservationState.Active
            : BuffAnchorObservationState.Inactive;
    }
}
