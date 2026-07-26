using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class GuideWindow : Window
{
    private readonly AppLog? _log;
    private readonly HashSet<string> _unavailableImages = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GuideTopicDisplay> _topics = [];

    public GuideWindow(AppLog? log = null)
    {
        _log = log;
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
        TopicTipList.ItemsSource = topic.Tips;
        OpenRelatedSectionButton.Content = topic.NavigationLabel;
        OpenRelatedSectionButton.Visibility = topic.Definition.NavigationTarget == GuideNavigationTarget.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpenExternalLinkButton.Visibility = topic.Definition.Id == GuideCatalog.TroubleshootingTopicId
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopicScrollViewer.ScrollToTop();
        HideGuideImagePopup();
    }

    private void GuideStep_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GuideStepDisplay step } ||
            !TryLoadGuideImage(step.ImageFileName))
        {
            HideGuideImagePopup();
            return;
        }

        UpdateGuideImagePopupPosition(e);
        GuideImagePopup.IsOpen = true;
    }

    private void GuideStep_MouseMove(object sender, MouseEventArgs e)
    {
        if (GuideImagePopup.IsOpen)
        {
            UpdateGuideImagePopupPosition(e);
        }
    }

    private void GuideStep_MouseLeave(object sender, MouseEventArgs e) => HideGuideImagePopup();

    private bool TryLoadGuideImage(string fileName)
    {
        if (_unavailableImages.Contains(fileName))
        {
            return false;
        }

        try
        {
            var uri = new Uri($"/Image/Guide/{fileName}", UriKind.Relative);
            var resource = Application.GetResourceStream(uri);
            if (resource is null)
            {
                _unavailableImages.Add(fileName);
                return false;
            }

            using (resource.Stream)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = resource.Stream;
                image.EndInit();
                image.Freeze();
                GuidePopupImage.Source = image;
            }
            return true;
        }
        catch (IOException)
        {
            // Guide images are optional. Missing files are expected until an image is supplied.
            _unavailableImages.Add(fileName);
            return false;
        }
        catch (Exception exception)
        {
            _unavailableImages.Add(fileName);
            _log?.Error($"Guide image could not be loaded: file={fileName}.", exception);
            return false;
        }
    }

    private void UpdateGuideImagePopupPosition(MouseEventArgs e)
    {
        var position = e.GetPosition(GuideRoot);
        GuideImagePopup.HorizontalOffset = position.X + 18;
        GuideImagePopup.VerticalOffset = position.Y + 18;
    }

    private void HideGuideImagePopup()
    {
        GuideImagePopup.IsOpen = false;
        GuidePopupImage.Source = null;
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
