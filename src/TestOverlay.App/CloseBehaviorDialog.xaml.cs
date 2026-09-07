using System.Windows;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog()
    {
        InitializeComponent();
    }

    public AppCloseBehavior Choice { get; private set; } = AppCloseBehavior.Ask;

    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = AppCloseBehavior.Exit;
        DialogResult = true;
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = AppCloseBehavior.MinimizeToTray;
        DialogResult = true;
    }
}
