using System.Windows;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class ProfileSaveFailureDialog : Window
{
    public ProfileSaveFailureDialog(string? errorDetail)
    {
        InitializeComponent();
        ErrorDetailText.Text = string.IsNullOrWhiteSpace(errorDetail)
            ? string.Empty
            : errorDetail;
    }

    public ProfileSaveFailureChoice Choice { get; private set; } = ProfileSaveFailureChoice.Cancel;

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ProfileSaveFailureChoice.Retry;
        DialogResult = true;
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ProfileSaveFailureChoice.DiscardAndExit;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = ProfileSaveFailureChoice.Cancel;
        DialogResult = false;
    }
}
