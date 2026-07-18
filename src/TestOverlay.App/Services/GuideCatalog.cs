using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class GuideCatalog
{
    public const string DefaultTopicId = "quick-start";
    public const string TroubleshootingTopicId = "troubleshooting";
    public const string GitHubIssuesUrl = "https://github.com/Nichipe-JCM/testoverlayproj/issues";

    public static IReadOnlyList<GuideTopicDefinition> Topics { get; } =
    [
        Topic("quick-start", "guide.topic.quick.title", "guide.topic.quick.summary", "guide.topic.quick.tip",
            GuideNavigationTarget.None, "guide.open.overlay", 5),
        Topic("profile-capture", "guide.topic.capture.title", "guide.topic.capture.summary", "guide.topic.capture.tip",
            GuideNavigationTarget.None, "guide.open.overlay", 4),
        Topic("quickslots", "guide.topic.quickslots.title", "guide.topic.quickslots.summary", "guide.topic.quickslots.tip",
            GuideNavigationTarget.None, "guide.open.overlay", 4),
        Topic("layout", "guide.topic.layout.title", "guide.topic.layout.summary", "guide.topic.layout.tip",
            GuideNavigationTarget.None, "guide.open.overlay", 4),
        Topic("monitors", "guide.topic.monitors.title", "guide.topic.monitors.summary", "guide.topic.monitors.tip",
            GuideNavigationTarget.MonitorAlerts, "guide.open.monitors", 4),
        Topic("custom-timers", "guide.topic.timers.title", "guide.topic.timers.summary", "guide.topic.timers.tip",
            GuideNavigationTarget.CustomTimers, "guide.open.timers", 4),
        Topic("erin-timer", "guide.topic.erin.title", "guide.topic.erin.summary", "guide.topic.erin.tip",
            GuideNavigationTarget.ErinTimer, "guide.open.erin", 3),
        Topic("compact-mode", "guide.topic.compact.title", "guide.topic.compact.summary", "guide.topic.compact.tip",
            GuideNavigationTarget.None, "guide.open.overlay", 3),
        Topic("settings", "guide.topic.settings.title", "guide.topic.settings.summary", "guide.topic.settings.tip",
            GuideNavigationTarget.Settings, "guide.open.settings", 4),
        Topic("troubleshooting", "guide.topic.troubleshooting.title", "guide.topic.troubleshooting.summary", "guide.topic.troubleshooting.tip",
            GuideNavigationTarget.Settings, "guide.open.settings", 4)
    ];

    public static IReadOnlyList<GuideTopicDisplay> LocalizeTopics() =>
        Topics.Select(definition => new GuideTopicDisplay(
            definition,
            L.T(definition.TitleKey),
            L.T(definition.SummaryKey),
            definition.StepKeys.Select(L.T).ToArray(),
            L.T(definition.TipKey),
            L.T(definition.NavigationLabelKey))).ToArray();

    private static GuideTopicDefinition Topic(
        string id,
        string titleKey,
        string summaryKey,
        string tipKey,
        GuideNavigationTarget target,
        string navigationLabelKey,
        int stepCount) =>
        new(
            id,
            titleKey,
            summaryKey,
            Enumerable.Range(1, stepCount).Select(index => $"guide.topic.{TopicKey(id)}.step.{index}").ToArray(),
            tipKey,
            target,
            navigationLabelKey);

    private static string TopicKey(string id) => id switch
    {
        "quick-start" => "quick",
        "profile-capture" => "capture",
        "custom-timers" => "timers",
        "erin-timer" => "erin",
        "compact-mode" => "compact",
        _ => id
    };
}
