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
    private string _activeProfileName;
    private string? _pendingProfileName;
    private bool _isNormalizingRuntimeSelection;

    public SettingsWindow(
        string profileDirectory,
        string defaultProfileDirectory,
        OverlayRenderMode selectedRenderMode,
        bool automaticRendererSelection,
        CaptureBackend selectedCaptureBackend,
        string selectedLanguage,
        string activeProfileName,
        string logPath,
        DateTimeOffset logSessionStartedAt)
    {
        InitializeComponent();
        _defaultProfileDirectory = defaultProfileDirectory;
        _logPath = logPath;
        _logSessionStartedAt = logSessionStartedAt;
        ProfileDirectory = profileDirectory;
        ProfileDirectoryBox.Text = profileDirectory;
        _activeProfileName = ProfileStore.NormalizeProfileName(activeProfileName);
        RefreshManagedProfiles(_activeProfileName);

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
            new(LocalizationService.Korean, L.T("language.korean")),
            new(LocalizationService.English, L.T("language.english"))
        };
        LanguageCombo.ItemsSource = languages;
        SelectLanguage(selectedLanguage);
    }

    public string ProfileDirectory { get; private set; }

    public OverlayRenderMode SelectedRenderMode { get; private set; }

    public bool AutomaticRendererSelection { get; private set; } = true;

    public CaptureBackend SelectedCaptureBackend { get; private set; }

    public string SelectedLanguage { get; private set; } = LocalizationService.Korean;

    public string ActiveProfileName => _activeProfileName;

    public string? RequestedProfileName { get; private set; }

    public bool ProfileApplyRequested { get; private set; }

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
            RefreshManagedProfiles(_activeProfileName);
        }
    }

    private void DefaultButton_Click(object sender, RoutedEventArgs e)
    {
        ProfileDirectoryBox.Text = _defaultProfileDirectory;
        RefreshManagedProfiles(_activeProfileName);
    }

    private void ProfileDirectoryBox_LostFocus(object sender, RoutedEventArgs e) =>
        RefreshManagedProfiles(_activeProfileName);

    private void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedProfileCombo.SelectedItem is not string profileName ||
            !CreateProfileStore().Exists(profileName))
        {
            ShowProfileError(L.T("profile.select.existing"));
            return;
        }

        _pendingProfileName = profileName;
        ProfileApplyConfirmationText.Text = L.F("profile.apply.confirm.message", profileName);
        ProfileApplyConfirmationOverlay.Visibility = Visibility.Visible;
    }

    private void CancelProfileApplyButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingProfileName = null;
        ProfileApplyConfirmationOverlay.Visibility = Visibility.Collapsed;
    }

    private void ConfirmProfileApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingProfileName))
        {
            ProfileApplyConfirmationOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        RequestedProfileName = _pendingProfileName;
        ProfileApplyRequested = true;
        ProfileApplyConfirmationOverlay.Visibility = Visibility.Collapsed;
        Commit();
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedProfileCombo.SelectedItem is not string currentName)
        {
            ShowProfileError(L.T("profile.select.existing"));
            return;
        }

        var dialog = new ProfileNameDialog(currentName, isRename: true) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var renamed = CreateProfileStore().Rename(currentName, dialog.ProfileName);
            if (string.Equals(_activeProfileName, currentName, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfileName = renamed;
            }
            RefreshManagedProfiles(renamed);
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
        }
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("profile.import"),
            Filter = L.T("profile.file.filter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var store = CreateProfileStore();
            var proposedName = ProfileStore.NormalizeProfileName(Path.GetFileNameWithoutExtension(dialog.FileName));
            if (store.Exists(proposedName) && MessageBox.Show(
                    this,
                    L.F("profile.import.replace.confirm", proposedName),
                    L.T("profile.import"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var imported = store.Import(dialog.FileName, proposedName);
            RefreshManagedProfiles(imported);
        }
        catch (Exception exception)
        {
            ShowProfileError(L.F("profile.import.failed", exception.Message));
        }
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedProfileCombo.SelectedItem is not string profileName)
        {
            ShowProfileError(L.T("profile.select.existing"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = L.T("profile.export"),
            Filter = L.T("profile.file.filter"),
            FileName = $"{profileName}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            CreateProfileStore().Export(profileName, dialog.FileName);
        }
        catch (Exception exception)
        {
            ShowProfileError(L.F("profile.export.failed", exception.Message));
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Commit();
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
        RefreshManagedProfiles(_activeProfileName);
        AutomaticRendererCheckBox.IsChecked = true;
        SelectRenderMode(OverlayRenderMode.GpuDxgi);
        SelectCaptureBackend(CaptureBackend.Wgc);
        SelectLanguage(LocalizationService.Korean);
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
                : LocalizationService.Korean;
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

    private ProfileStore CreateProfileStore() => new(GetProfileDirectoryFromBox());

    private string GetProfileDirectoryFromBox()
    {
        var directory = ProfileDirectoryBox.Text.Trim();
        return string.IsNullOrWhiteSpace(directory)
            ? _defaultProfileDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
    }

    private void RefreshManagedProfiles(string? preferredName)
    {
        try
        {
            var names = CreateProfileStore().ListProfileNames();
            ManagedProfileCombo.ItemsSource = names;
            ManagedProfileCombo.SelectedItem = names.FirstOrDefault(name =>
                string.Equals(name, preferredName, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            ActiveProfileText.Text = L.F("profile.active.arg", _activeProfileName);
        }
        catch (Exception exception)
        {
            ManagedProfileCombo.ItemsSource = Array.Empty<string>();
            ActiveProfileText.Text = L.F("profile.folder.invalid", exception.Message);
        }
    }

    private void ShowProfileError(string message)
    {
        MessageBox.Show(this, message, L.T("Profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
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

