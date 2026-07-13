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
    private bool _isNormalizingRuntimeSelection;

    public SettingsWindow(
        string profileDirectory,
        string defaultProfileDirectory,
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

        var renderModes = new List<RenderModeOption>
        {
            new(OverlayRenderMode.CpuWpf, L.T("Existing CPU/WPF")),
            new(OverlayRenderMode.GpuDxgi, L.T("GPU/DXGI")),
            new(OverlayRenderMode.CpuComposited, L.T("Improved CPU/Composited"))
        };
        RenderModeCombo.ItemsSource = renderModes;
        SelectRenderMode(selectedRenderMode);

        var captureBackends = new List<CaptureBackendOption>
        {
            new(CaptureBackend.DxgiDesktopDuplication, L.T("DXGI monitor")),
            new(CaptureBackend.Wgc, L.T("WGC window")),
            new(CaptureBackend.GdiBitBlt, L.T("GDI BitBlt"))
        };
        CaptureBackendCombo.ItemsSource = captureBackends;
        SelectCaptureBackend(selectedCaptureBackend);
        NormalizeRuntimeSelection(preferRenderer: true);
        RenderModeCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection(preferRenderer: true);
        CaptureBackendCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection(preferRenderer: false);

        var languages = new List<LanguageOption>
        {
            new(LocalizationService.English, L.T("language.english")),
            new(LocalizationService.Korean, L.T("language.korean"))
        };
        LanguageCombo.ItemsSource = languages;
        SelectLanguage(selectedLanguage);
    }

    public string ProfileDirectory { get; private set; }

    public OverlayRenderMode SelectedRenderMode { get; private set; }

    public CaptureBackend SelectedCaptureBackend { get; private set; }

    public string SelectedLanguage { get; private set; } = LocalizationService.English;

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
        Commit();
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

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var firstConfirm = MessageBox.Show(
            this,
            L.T("reset.settings.confirm.message"),
            L.T("reset.settings"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (firstConfirm != MessageBoxResult.Yes)
        {
            return;
        }

        var secondConfirm = MessageBox.Show(
            this,
            L.T("reset.settings.second.confirm.message"),
            L.T("reset.settings"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (secondConfirm != MessageBoxResult.Yes)
        {
            return;
        }

        ProfileDirectoryBox.Text = _defaultProfileDirectory;
        SelectRenderMode(OverlayRenderMode.GpuDxgi);
        SelectCaptureBackend(CaptureBackend.Wgc);
        SelectLanguage(LocalizationService.English);
    }

    private void Commit()
    {
        var directory = ProfileDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _defaultProfileDirectory;
        }

        try
        {
            ProfileDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
            SelectedRenderMode = RenderModeCombo.SelectedItem is RenderModeOption option
                ? option.Mode
                : OverlayRenderMode.CpuWpf;
            SelectedCaptureBackend = CaptureBackendCombo.SelectedItem is CaptureBackendOption captureOption
                ? captureOption.Backend
                : CaptureBackend.Wgc;
            SelectedLanguage = LanguageCombo.SelectedItem is LanguageOption languageOption
                ? languageOption.Language
                : LocalizationService.English;
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

    private void SelectRenderMode(OverlayRenderMode mode)
    {
        RenderModeCombo.SelectedItem = RenderModeCombo.Items.OfType<RenderModeOption>()
            .FirstOrDefault(option => option.Mode == mode)
            ?? RenderModeCombo.Items.OfType<RenderModeOption>().FirstOrDefault();
    }

    private void SelectCaptureBackend(CaptureBackend backend)
    {
        CaptureBackendCombo.SelectedItem = CaptureBackendCombo.Items.OfType<CaptureBackendOption>()
            .FirstOrDefault(option => option.Backend == backend)
            ?? CaptureBackendCombo.Items.OfType<CaptureBackendOption>()
                .FirstOrDefault(option => option.Backend == CaptureBackend.DxgiDesktopDuplication);
    }

    private void NormalizeRuntimeSelection(bool preferRenderer)
    {
        if (_isNormalizingRuntimeSelection)
        {
            return;
        }

        var renderMode = (RenderModeCombo.SelectedItem as RenderModeOption)?.Mode;
        var captureBackend = (CaptureBackendCombo.SelectedItem as CaptureBackendOption)?.Backend;
        if (renderMode is null || captureBackend is null)
        {
            return;
        }

        var normalized = RuntimeConfigurationPolicy.Normalize(
            renderMode.Value,
            captureBackend.Value,
            preferRenderer
                ? RuntimeSelectionPreference.Renderer
                : RuntimeSelectionPreference.CaptureBackend);
        if (normalized.RenderMode == renderMode && normalized.CaptureBackend == captureBackend)
        {
            return;
        }

        _isNormalizingRuntimeSelection = true;
        try
        {
            SelectRenderMode(normalized.RenderMode);
            SelectCaptureBackend(normalized.CaptureBackend);
        }
        finally
        {
            _isNormalizingRuntimeSelection = false;
        }
    }

    private void SelectLanguage(string language)
    {
        var normalized = LocalizationService.NormalizeLanguage(language);
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<LanguageOption>()
            .FirstOrDefault(option => string.Equals(option.Language, normalized, StringComparison.OrdinalIgnoreCase))
            ?? LanguageCombo.Items.OfType<LanguageOption>().FirstOrDefault();
    }

    private sealed record RenderModeOption(OverlayRenderMode Mode, string Label);

    private sealed record CaptureBackendOption(CaptureBackend Backend, string Label);

    private sealed record LanguageOption(string Language, string Label);
}

