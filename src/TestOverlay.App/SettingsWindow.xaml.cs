using System.IO;
using System.Windows;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class SettingsWindow : Window
{
    private readonly string _defaultProfileDirectory;
    private readonly string _logPath;
    private readonly DateTimeOffset _logSessionStartedAt;

    public SettingsWindow(
        string profileDirectory,
        string defaultProfileDirectory,
        IReadOnlyList<string> profileNames,
        string selectedProfileName,
        OverlayRenderMode selectedRenderMode,
        CaptureBackend selectedCaptureBackend,
        string selectedLanguage,
        string logPath,
        DateTimeOffset logSessionStartedAt)
    {
        InitializeComponent();
        _defaultProfileDirectory = defaultProfileDirectory;
        _logPath = logPath;
        _logSessionStartedAt = logSessionStartedAt;
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
            new(OverlayRenderMode.CpuWpf, L.T("Existing CPU/WPF")),
            new(OverlayRenderMode.GpuDxgi, L.T("GPU/DXGI")),
            new(OverlayRenderMode.CpuComposited, L.T("Improved CPU/Composited"))
        };
        RenderModeCombo.ItemsSource = renderModes;
        RenderModeCombo.SelectedItem = renderModes.FirstOrDefault(option => option.Mode == selectedRenderMode)
                                       ?? renderModes[0];

        var captureBackends = new List<CaptureBackendOption>
        {
            new(CaptureBackend.Wgc, L.T("WGC window")),
            new(CaptureBackend.DxgiDesktopDuplication, L.T("DXGI monitor")),
            new(CaptureBackend.GdiBitBlt, L.T("GDI BitBlt"))
        };
        CaptureBackendCombo.ItemsSource = captureBackends;
        CaptureBackendCombo.SelectedItem = captureBackends.FirstOrDefault(option => option.Backend == selectedCaptureBackend)
                                           ?? captureBackends[0];

        var languages = new List<LanguageOption>
        {
            new(LocalizationService.English, "English"),
            new(LocalizationService.Korean, "\uD55C\uAD6D\uC5B4")
        };
        LanguageCombo.ItemsSource = languages;
        LanguageCombo.SelectedItem = languages.FirstOrDefault(option =>
            string.Equals(option.Language, LocalizationService.NormalizeLanguage(selectedLanguage), StringComparison.OrdinalIgnoreCase)) ?? languages[0];
    }

    public string ProfileDirectory { get; private set; }

    public string SelectedProfileName { get; private set; }

    public OverlayRenderMode SelectedRenderMode { get; private set; }

    public CaptureBackend SelectedCaptureBackend { get; private set; }

    public string SelectedLanguage { get; private set; } = LocalizationService.English;

    public SettingsProfileAction RequestedProfileAction { get; private set; } = SettingsProfileAction.None;

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("Choose profile save folder"),
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

    private void BenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new BenchmarkWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new LogWindow(_logPath, _logSessionStartedAt)
        {
            Owner = this
        };
        window.ShowDialog();
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
            SelectedCaptureBackend = CaptureBackendCombo.SelectedItem is CaptureBackendOption captureOption
                ? captureOption.Backend
                : CaptureBackend.Wgc;
            SelectedLanguage = LanguageCombo.SelectedItem is LanguageOption languageOption
                ? languageOption.Language
                : LocalizationService.English;
            RequestedProfileAction = action;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                L.T("Invalid folder"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private sealed record RenderModeOption(OverlayRenderMode Mode, string Label);

    private sealed record CaptureBackendOption(CaptureBackend Backend, string Label);

    private sealed record LanguageOption(string Language, string Label);
}

public enum SettingsProfileAction
{
    None,
    Save,
    Load
}
