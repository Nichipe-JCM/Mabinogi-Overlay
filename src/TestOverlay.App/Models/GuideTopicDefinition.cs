namespace TestOverlay.App.Models;

public enum GuideNavigationTarget
{
    None,
    Overlay,
    MonitorAlerts,
    CustomTimers,
    ErinTimer,
    Settings
}

public sealed record GuideTopicDefinition(
    string Id,
    string TitleKey,
    string SummaryKey,
    IReadOnlyList<string> StepKeys,
    string TipKey,
    GuideNavigationTarget NavigationTarget,
    string NavigationLabelKey);

public sealed record GuideTopicDisplay(
    GuideTopicDefinition Definition,
    string Title,
    string Summary,
    IReadOnlyList<string> Steps,
    string Tip,
    string NavigationLabel);
