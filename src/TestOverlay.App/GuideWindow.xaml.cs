using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class GuideWindow : Window
{
    private IReadOnlyList<GuideTopicDisplay> _topics = [];

    public GuideWindow()
    {
        InitializeComponent();
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
        Closed += (_, _) => LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
        ReloadTopics(GuideCatalog.DefaultTopicId);
    }

    public event EventHandler<GuideNavigationRequestedEventArgs>? NavigationRequested;

    public void SelectTopic(string topicId)
    {
        var topic = _topics.FirstOrDefault(item => item.Definition.Id == topicId);
        if (topic is not null)
        {
            TopicList.SelectedItem = topic;
        }
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        var selectedId = (TopicList.SelectedItem as GuideTopicDisplay)?.Definition.Id ?? GuideCatalog.DefaultTopicId;
        ReloadTopics(selectedId);
    }

    private void ReloadTopics(string selectedId)
    {
        _topics = GuideCatalog.LocalizeTopics();
        TopicList.ItemsSource = _topics;
        SelectTopic(selectedId);
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TopicList.SelectedItem is not GuideTopicDisplay topic)
        {
            return;
        }

        TopicTitleText.Text = topic.Title;
        TopicSummaryText.Text = topic.Summary;
        StepList.ItemsSource = topic.Steps;
        TopicTipText.Text = topic.Tip;
        TopicTipBorder.Visibility = string.IsNullOrWhiteSpace(topic.Tip)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpenRelatedSectionButton.Content = topic.NavigationLabel;
        OpenRelatedSectionButton.Visibility = topic.Definition.NavigationTarget == GuideNavigationTarget.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpenExternalLinkButton.Visibility = topic.Definition.Id == GuideCatalog.TroubleshootingTopicId
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopicScrollViewer.ScrollToTop();
    }

    private void OpenExternalLinkButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(GuideCatalog.GitHubIssuesUrl)
        {
            UseShellExecute = true
        });
    }

    private void OpenRelatedSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (TopicList.SelectedItem is GuideTopicDisplay topic)
        {
            NavigationRequested?.Invoke(this, new GuideNavigationRequestedEventArgs(topic.Definition.NavigationTarget));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class GuideNavigationRequestedEventArgs(GuideNavigationTarget target) : EventArgs
{
    public GuideNavigationTarget Target { get; } = target;
}
