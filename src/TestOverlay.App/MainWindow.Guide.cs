using System.Windows;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class MainWindow
{
    private GuideWindow? _guideWindow;

    internal void OpenGuide(string topicId = Services.GuideCatalog.DefaultTopicId)
    {
        if (_guideWindow is null)
        {
            _guideWindow = new GuideWindow();
            _guideWindow.NavigationRequested += GuideWindow_NavigationRequested;
            _guideWindow.Closed += (_, _) => _guideWindow = null;
        }

        _guideWindow.SelectTopic(topicId);
        _guideWindow.Show();
        _guideWindow.WindowState = WindowState.Normal;
        _guideWindow.Activate();
    }

    private void GuideButton_Click(object sender, RoutedEventArgs e) => OpenGuide();

    private void GuideWindow_NavigationRequested(object? sender, GuideNavigationRequestedEventArgs e)
    {
        if (!IsVisible)
        {
            RestoreFullMode();
        }
        else
        {
            WindowState = WindowState.Normal;
            Activate();
        }

        switch (e.Target)
        {
            case GuideNavigationTarget.Overlay:
                RightPanelTabs.SelectedItem = OverlayTabItem;
                break;
            case GuideNavigationTarget.MonitorAlerts:
                RightPanelTabs.SelectedItem = MonitorAlertsTabItem;
                break;
            case GuideNavigationTarget.CustomTimers:
                RightPanelTabs.SelectedItem = CustomTimerTabItem;
                break;
            case GuideNavigationTarget.ErinTimer:
                RightPanelTabs.SelectedItem = ErinTimerTabItem;
                break;
            case GuideNavigationTarget.Settings:
                SettingsButton_Click(SettingsButton, new RoutedEventArgs());
                break;
        }
    }

    private void CloseGuideWindow()
    {
        if (_guideWindow is null)
        {
            return;
        }

        _guideWindow.NavigationRequested -= GuideWindow_NavigationRequested;
        _guideWindow.Close();
        _guideWindow = null;
    }
}
