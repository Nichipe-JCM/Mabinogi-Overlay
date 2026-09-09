using System.IO;
using System.Windows;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class SettingsWindow : Window
{
    private readonly string _defaultProfileDirectory;
    private readonly string _initialProfileDirectory;
    private readonly string _logPath;
    private readonly DateTimeOffset _logSessionStartedAt;
    private string _activeProfileName;
    private string? _pendingProfileName;
    private string? _pendingDeleteProfileName;
    private int _deleteConfirmationStage;
    private bool _isNormalizingRuntimeSelection;
    private readonly UpdateCoordinator? _updates;
    private readonly System.Windows.Threading.DispatcherTimer _updateClock = new() { Interval = TimeSpan.FromSeconds(1) };
    public bool UpdateRequested { get; private set; }

    public SettingsWindow(
        string profileDirectory,
        string defaultProfileDirectory,
        OverlayRenderMode selectedRenderMode,
        bool automaticRendererSelection,
        bool automaticCaptureSelection,
        CaptureBackend selectedCaptureBackend,
        string selectedLanguage,
        AppCloseBehavior closeBehavior,
        bool saveOcrDiagnosticImages,
        bool profileRecoveryRequired,
        string activeProfileName,
        string logPath,
        DateTimeOffset logSessionStartedAt,
        UpdateCoordinator? updates = null)
    {
        InitializeComponent();
        _updates = updates;
        if (_updates is not null) _updates.Changed += UpdateStateChanged;
        _updateClock.Tick += (_, _) => RefreshUpdateInfo();
        _updateClock.Start();
        Closed += (_, _) => { _updateClock.Stop(); if (_updates is not null) _updates.Changed -= UpdateStateChanged; };
        RefreshUpdateInfo();
        _defaultProfileDirectory = defaultProfileDirectory;
        _initialProfileDirectory = Path.GetFullPath(profileDirectory);
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
        AutomaticCaptureCheckBox.IsChecked = automaticCaptureSelection;
        NormalizeRuntimeSelection();
        AutomaticRendererCheckBox.Click += (_, _) => NormalizeRuntimeSelection();
        AutomaticCaptureCheckBox.Click += (_, _) => NormalizeRuntimeSelection();
        RenderModeCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection();
        CaptureBackendCombo.SelectionChanged += (_, _) => NormalizeRuntimeSelection();

        var languages = new List<LanguageOption>
        {
            new(LocalizationService.Korean, L.T("language.korean")),
            new(LocalizationService.English, L.T("language.english"))
        };
        LanguageCombo.ItemsSource = languages;
        SelectLanguage(selectedLanguage);

        CloseBehaviorCombo.ItemsSource = new List<CloseBehaviorOption>
        {
            new(AppCloseBehavior.Ask, L.T("settings.close.behavior.ask")),
            new(AppCloseBehavior.Exit, L.T("settings.close.behavior.exit")),
            new(AppCloseBehavior.MinimizeToTray, L.T("settings.close.behavior.tray"))
        };
        SelectCloseBehavior(closeBehavior);
        SaveOcrDiagnosticImagesCheckBox.IsChecked = saveOcrDiagnosticImages;
        ProfileRecoveryNotice.Visibility = profileRecoveryRequired ? Visibility.Visible : Visibility.Collapsed;
        ProfileManagementPanel.IsEnabled = !profileRecoveryRequired;
        AboutVersionText.Text = L.F("settings.about.version.arg", AppVersion.DisplayVersion);
        SettingsSectionList.SelectedIndex = profileRecoveryRequired ? 1 : 0;
    }

    public string ProfileDirectory { get; private set; }

    private void UpdateStateChanged(object? sender, EventArgs e) => RefreshUpdateInfo();
    private void RefreshUpdateInfo()
    {
        UpdateInfoText.Text = L.T(MainWindow.UpdateStatusKey(_updates?.Status ?? UpdateStatus.Unknown));
        if (_updates?.Offer is { } offer) UpdateInfoText.Text += Environment.NewLine + L.F("update.new.version", offer.Release.Version);
        var wait = _updates?.CheckWaitSeconds ?? 0;
        UpdateCooldownText.Text = wait > 0 ? L.F("update.cooldown", wait) : L.T("update.interval");
        CheckUpdateButton.IsEnabled = _updates is not null && _updates.Status != UpdateStatus.Checking && wait == 0;
        OpenUpdateButton.IsEnabled = _updates?.Status == UpdateStatus.Available;
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) { if (_updates is not null) await _updates.CheckAsync(); }
    private void OpenUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updates?.Offer is null) return;
        UpdateRequested = true;
        OkButton_Click(sender, e);
        if (DialogResult != true) UpdateRequested = false;
    }

    public OverlayRenderMode SelectedRenderMode { get; private set; }

    public bool AutomaticRendererSelection { get; private set; } = true;

    public bool AutomaticCaptureSelection { get; private set; } = true;

    public CaptureBackend SelectedCaptureBackend { get; private set; }

    public string SelectedLanguage { get; private set; } = LocalizationService.Korean;

    public AppCloseBehavior SelectedCloseBehavior { get; private set; } = AppCloseBehavior.Ask;

    public bool SaveOcrDiagnosticImages { get; private set; }

    public string ActiveProfileName => _activeProfileName;

    public string? RequestedProfileName { get; private set; }

    public bool ProfileApplyRequested { get; private set; }

    public bool ActiveProfileRenamed { get; private set; }

    public bool ActiveProfileDeleted { get; private set; }

    public bool ProfileListChanged { get; private set; }

    private void SettingsSectionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selectedTag = (SettingsSectionList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as string ?? "General";
        GeneralSectionPanel.Visibility = selectedTag == "General" ? Visibility.Visible : Visibility.Collapsed;
        ProfileSectionPanel.Visibility = selectedTag == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSectionPanel.Visibility = selectedTag == "Runtime" ? Visibility.Visible : Visibility.Collapsed;
        AboutSectionPanel.Visibility = selectedTag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

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
            var store = CreateProfileStore();
            var renamed = store.Rename(currentName, dialog.ProfileName);
            ProfileListChanged = true;
            if (string.Equals(_activeProfileName, currentName, StringComparison.OrdinalIgnoreCase) &&
                PathsEqual(store.ProfileDirectory, _initialProfileDirectory))
            {
                _activeProfileName = renamed;
                ActiveProfileRenamed = true;
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
            ProfileListChanged = true;
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
            Filter = L.T("profile.package.filter"),
            FileName = $"{profileName}{ProfileStore.ProfilePackageExtension}",
            AddExtension = true,
            DefaultExt = ProfileStore.ProfilePackageExtension
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

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedProfileCombo.SelectedItem is not string profileName)
        {
            ShowProfileError(L.T("profile.select.existing"));
            return;
        }

        try
        {
            if (CreateProfileStore().ListProfileNames().Count <= 1)
            {
                ShowProfileError(L.T("profile.delete.last.disallowed"));
                return;
            }
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
            return;
        }

        _pendingDeleteProfileName = profileName;
        _deleteConfirmationStage = 1;
        ProfileDeleteConfirmationTitle.Text = L.T("profile.delete.confirm.first.title");
        ProfileDeleteConfirmationText.Text = L.F("profile.delete.confirm.first.message", profileName);
        ProfileDeleteWarningText.Text = L.T("profile.delete.irreversible");
        ConfirmProfileDeleteButton.Content = L.T("profile.delete.continue");
        ProfileDeleteConfirmationOverlay.Visibility = Visibility.Visible;
    }

    private void CancelProfileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingDeleteProfileName = null;
        _deleteConfirmationStage = 0;
        ProfileDeleteConfirmationOverlay.Visibility = Visibility.Collapsed;
    }

    private void ConfirmProfileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingDeleteProfileName))
        {
            CancelProfileDeleteButton_Click(sender, e);
            return;
        }

        if (_deleteConfirmationStage == 1)
        {
            _deleteConfirmationStage = 2;
            ProfileDeleteConfirmationTitle.Text = L.T("profile.delete.confirm.second.title");
            ProfileDeleteConfirmationText.Text = L.F(
                "profile.delete.confirm.second.message",
                _pendingDeleteProfileName);
            ProfileDeleteWarningText.Text = L.T("profile.delete.irreversible.strong");
            ConfirmProfileDeleteButton.Content = L.T("profile.delete.permanently");
            return;
        }

        try
        {
            var store = CreateProfileStore();
            var deletedName = _pendingDeleteProfileName;
            var deletedActiveProfile =
                string.Equals(_activeProfileName, deletedName, StringComparison.OrdinalIgnoreCase) &&
                PathsEqual(store.ProfileDirectory, _initialProfileDirectory);
            store.Delete(deletedName);
            ProfileListChanged = true;
            var remainingNames = store.ListProfileNames();
            if (remainingNames.Count == 0)
            {
                throw new InvalidOperationException(L.T("profile.delete.last.disallowed"));
            }

            var preferredName = deletedActiveProfile
                ? remainingNames[0]
                : _activeProfileName;
            if (deletedActiveProfile)
            {
                _activeProfileName = preferredName;
                ActiveProfileDeleted = true;
                ActiveProfileRenamed = false;
            }
            RefreshManagedProfiles(preferredName);
            CancelProfileDeleteButton_Click(sender, e);
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
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
        AutomaticCaptureCheckBox.IsChecked = true;
        SelectRenderMode(OverlayRenderMode.GpuDxgi);
        SelectCaptureBackend(CaptureBackend.Wgc);
        SelectLanguage(LocalizationService.Korean);
        SelectCloseBehavior(AppCloseBehavior.Ask);
        SaveOcrDiagnosticImagesCheckBox.IsChecked = false;
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
            AutomaticCaptureSelection = AutomaticCaptureCheckBox.IsChecked == true;
            SelectedCaptureBackend = CaptureBackendCombo.SelectedItem is CaptureBackendOption captureOption
                ? captureOption.Backend
                : CaptureBackend.Wgc;
            SelectedLanguage = LanguageCombo.SelectedItem is LanguageOption languageOption
                ? languageOption.Language
                : LocalizationService.Korean;
            SelectedCloseBehavior = CloseBehaviorCombo.SelectedItem is CloseBehaviorOption closeBehaviorOption
                ? closeBehaviorOption.Behavior
                : AppCloseBehavior.Ask;
            SaveOcrDiagnosticImages = SaveOcrDiagnosticImagesCheckBox.IsChecked == true;
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

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

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
        var automaticCapture = AutomaticCaptureCheckBox.IsChecked == true;
        CaptureBackendCombo.IsEnabled = !automaticCapture;
        if (automaticCapture && captureBackend != CaptureBackend.Wgc)
        {
            _isNormalizingRuntimeSelection = true;
            try
            {
                SelectCaptureBackend(CaptureBackend.Wgc);
                captureBackend = CaptureBackend.Wgc;
            }
            finally
            {
                _isNormalizingRuntimeSelection = false;
            }
        }
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

    private void SelectCloseBehavior(AppCloseBehavior behavior)
    {
        CloseBehaviorCombo.SelectedItem = CloseBehaviorCombo.Items.OfType<CloseBehaviorOption>()
            .FirstOrDefault(option => option.Behavior == behavior)
            ?? CloseBehaviorCombo.Items.OfType<CloseBehaviorOption>().FirstOrDefault();
    }

    private sealed record RenderModeOption(OverlayRenderMode Mode, string Label);

    private sealed record CaptureBackendOption(CaptureBackend Backend, string Label);

    private sealed record LanguageOption(string Language, string Label);

    private sealed record CloseBehaviorOption(AppCloseBehavior Behavior, string Label);
}

