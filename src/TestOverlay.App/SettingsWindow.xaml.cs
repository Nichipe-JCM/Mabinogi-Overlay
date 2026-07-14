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
        bool automaticRendererSelection,
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
            new(OverlayRenderMode.GpuDxgi, L.T("renderer.gpu.accelerated")),
            new(OverlayRenderMode.CpuComposited, L.T("renderer.cpu.composited")),
            new(OverlayRenderMode.CpuWpf, L.T("renderer.wpf.compatibility"))
        };
        RenderModeCombo.ItemsSource = renderModes;
        SelectRenderMode(selectedRenderMode);
        AutomaticRendererCheckBox.IsChecked = automaticRendererSelection;

        var captureBackends = new List<CaptureBackendOption>
        {
            new(CaptureBackend.DxgiDesktopDuplication, L.T("DXGI monitor")),
            new(CaptureBackend.Wgc, L.T("WGC window")),
            new(CaptureBackend.GdiBitBlt, L.T("GDI BitBlt"))
        };
        CaptureBackendCombo.ItemsSource = captureBackends;
        SelectCaptureBackend(selectedCaptureBackend);
        NormalizeRuntimeSelection();
        AutomaticRendererCheckBox.Click += (_, _) => NormalizeRuntimeSelection();
        RenderModeCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection();
        CaptureBackendCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection();

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

    public bool AutomaticRendererSelection { get; private set; } = true;

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
        AutomaticRendererCheckBox.IsChecked = true;
        SelectRenderMode(OverlayRenderMode.GpuDxgi);
        SelectCaptureBackend(CaptureBackend.Wgc);
        SelectLanguage(LocalizationService.English);
        NormalizeRuntimeSelection();
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
            AutomaticRendererSelection = AutomaticRendererCheckBox.IsChecked == true;
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

    private void NormalizeRuntimeSelection()
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

        var automatic = AutomaticRendererCheckBox.IsChecked == true;
        RenderModeCombo.IsEnabled = !automatic;
        if (!automatic &&
            renderMode == OverlayRenderMode.GpuDxgi &&
            captureBackend != CaptureBackend.Wgc)
        {
            OkButton.IsEnabled = false;
            RendererSelectionSummaryText.Text = L.T("renderer.manual.summary.gpu.incompatible");
            return;
        }

        OkButton.IsEnabled = true;
        var requestedRenderMode = automatic
            ? RuntimeConfigurationPolicy.ResolveAutomaticRenderer(captureBackend.Value)
            : renderMode.Value;
        var normalized = RuntimeConfigurationPolicy.Normalize(
            requestedRenderMode,
            captureBackend.Value,
            RuntimeSelectionPreference.CaptureBackend);
        if (normalized.RenderMode != renderMode || normalized.CaptureBackend != captureBackend)
        {
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

        RendererSelectionSummaryText.Text = BuildRendererSelectionSummary(
            automatic,
            normalized.RenderMode,
            normalized.CaptureBackend);
    }

    private static string BuildRendererSelectionSummary(
        bool automatic,
        OverlayRenderMode renderMode,
        CaptureBackend captureBackend)
    {
        if (automatic)
        {
            return captureBackend switch
            {
                CaptureBackend.Wgc => L.T("renderer.auto.summary.wgc"),
                CaptureBackend.DxgiDesktopDuplication => L.T("renderer.auto.summary.dxgi"),
                _ => L.T("renderer.auto.summary.gdi")
            };
        }

        return renderMode switch
        {
            OverlayRenderMode.GpuDxgi => L.T("renderer.manual.summary.gpu"),
            OverlayRenderMode.CpuComposited => L.T("renderer.manual.summary.cpu"),
            _ => L.T("renderer.manual.summary.wpf")
        };
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

