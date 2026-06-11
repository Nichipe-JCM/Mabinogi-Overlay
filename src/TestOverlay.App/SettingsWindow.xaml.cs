using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TestOverlay.App;

public partial class SettingsWindow : Window
{
    private readonly string _defaultProfileDirectory;

    public SettingsWindow(string profileDirectory, string defaultProfileDirectory)
    {
        InitializeComponent();
        _defaultProfileDirectory = defaultProfileDirectory;
        ProfileDirectory = profileDirectory;
        ProfileDirectoryBox.Text = profileDirectory;
    }

    public string ProfileDirectory { get; private set; }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose profile save folder",
            InitialDirectory = Directory.Exists(ProfileDirectoryBox.Text)
                ? ProfileDirectoryBox.Text
                : _defaultProfileDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            ProfileDirectoryBox.Text = dialog.FolderName;
        }
    }

    private void DefaultButton_Click(object sender, RoutedEventArgs e)
    {
        ProfileDirectoryBox.Text = _defaultProfileDirectory;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = ProfileDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _defaultProfileDirectory;
        }

        try
        {
            ProfileDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Invalid folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
