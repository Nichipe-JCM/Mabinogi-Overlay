using System.IO;
using System.Windows;
using Microsoft.Win32;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class SettingsWindow : Window
{
    private readonly string _defaultProfileDirectory;

    public SettingsWindow(
        string profileDirectory,
        string defaultProfileDirectory,
        IReadOnlyList<string> profileNames,
        string selectedProfileName,
        OverlayRenderMode selectedRenderMode)
    {
        InitializeComponent();
        _defaultProfileDirectory = defaultProfileDirectory;
        ProfileDirectory = profileDirectory;
        ProfileDirectoryBox.Text = profileDirectory;
        SelectedProfileName = string.IsNullOrWhiteSpace(selectedProfileName) ? "default" : selectedProfileName.Trim();
        var names = profileNames.Count == 0
            ? new List<string> { "default" }
            : profileNames.ToList();
        if (!names.Contains(SelectedProfileName, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(SelectedProfileName);
            names = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        ProfileCombo.ItemsSource = names;
        ProfileCombo.SelectedItem = names.FirstOrDefault(name => string.Equals(name, SelectedProfileName, StringComparison.OrdinalIgnoreCase))
                                    ?? names.FirstOrDefault();

        var renderModes = new List<RenderModeOption>
        {
            new(OverlayRenderMode.CpuWpf, "Existing CPU/WPF"),
            new(OverlayRenderMode.GpuDxgi, "Current GPU/DXGI"),
            new(OverlayRenderMode.CpuComposited, "Improved CPU/Composited")
        };
        RenderModeCombo.ItemsSource = renderModes;
        RenderModeCombo.SelectedItem = renderModes.FirstOrDefault(option => option.Mode == selectedRenderMode)
                                       ?? renderModes[0];
    }

    public string ProfileDirectory { get; private set; }

    public string SelectedProfileName { get; private set; }

    public OverlayRenderMode SelectedRenderMode { get; private set; }

    public SettingsProfileAction RequestedProfileAction { get; private set; } = SettingsProfileAction.None;

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
        Commit(SettingsProfileAction.None);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        Commit(SettingsProfileAction.Save);
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
    {
        Commit(SettingsProfileAction.Load);
    }

    private void Commit(SettingsProfileAction action)
    {
        var directory = ProfileDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _defaultProfileDirectory;
        }

        try
        {
            ProfileDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
            SelectedProfileName = ProfileCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected)
                ? selected.Trim()
                : "default";
            SelectedRenderMode = RenderModeCombo.SelectedItem is RenderModeOption option
                ? option.Mode
                : OverlayRenderMode.CpuWpf;
            RequestedProfileAction = action;
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

    private sealed record RenderModeOption(OverlayRenderMode Mode, string Label);
}

public enum SettingsProfileAction
{
    None,
    Save,
    Load
}
