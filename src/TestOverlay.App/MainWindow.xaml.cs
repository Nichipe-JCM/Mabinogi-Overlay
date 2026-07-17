using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow : Window
{
    private const int CandidateBorderPixels = 1;
    private const int CandidateVisualPaddingPixels = 1;
    private const int DebugDetectRuns = 100;
    private const double MinimumOverlaySlotSize = 1;
    private const int MonitorRecognitionIntervalSeconds = 2;
    private const int TuairimNormalChargeSecondsPerPercent = 6;
    private const int TuairimFullEffectSeconds = 20;
    private static readonly Color ProjectAccentColor = Color.FromRgb(0x89, 0xDE, 0xD4);
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly CaptureSessionCoordinator _captureSession;
    private readonly OverlayRuntimeController _overlayRuntime;
    private readonly AlertAudioService _alertAudio;
    private readonly StatusObservationController _statusObservations;
    private readonly MonitorRecognitionRetryPolicy _monitorRecognitionRetryPolicy = new();
    private readonly RoiSectionDetectionService _roiSectionDetection = new();
    private readonly MonitorTemplateDetectionService _monitorTemplateDetection = new();
    private readonly MonitorValueRecognitionService _monitorValueRecognition = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ProfileStore _profileStore;
    private readonly ProfileSession _profileSession;
    private AppSettings _appSettings;
    private readonly AppLog _log;
    private readonly object _detectLogSync = new();
    private readonly string _detectSessionLogPath;
    private readonly DispatcherTimer _profileAutoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _internalTimerDebugTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _inAppNoticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly OverlayWorkspaceState _workspace = new();
    private readonly CandidateWorkspace _candidateWorkspace;
    private ObservableCollection<SlotCandidate> _candidates => _workspace.Candidates;
    private ObservableCollection<QuickslotSection> _sections => _workspace.Sections;
    private List<OverlaySlot> _overlaySlots => _workspace.OverlaySlots;
    private List<InternalBuffTimer> _internalBuffTimers => _statusObservations.Timers;
    private readonly HashSet<string> _recognizedBuffNameKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedBuffNameKeys = new(StringComparer.Ordinal);
    private HashSet<string> _pendingInitialBuffMinuteValidation =>
        _statusObservations.PendingInitialBuffValidation;
    private readonly Dictionary<string, BuffIconMatch> _buffIconMatches = new(StringComparer.Ordinal);
    private readonly HashSet<OverlayElementKind> _hiddenMonitorElementKinds = [];
    private readonly HashSet<string> _monitorDiagnosticKindsSaved = new(StringComparer.Ordinal);
    private readonly List<Rectangle> _monitorDetectionRects = new();
    private readonly Dictionary<SlotCandidate, Rectangle> _candidateRects = new();
    private SectionSettings[] _sectionSettings => _workspace.SectionSettings;
    private IReadOnlyList<string> _profileNames => _profileSession.ProfileNames;
    private string _selectedProfileName
    {
        get => _profileSession.SelectedProfileName;
        set => _profileSession.SelectedProfileName = value;
    }
    private bool _isUpdatingProfileSelection;
    private bool _isLoadingProfile
    {
        get => _profileSession.IsLoading;
        set => _profileSession.IsLoading = value;
    }
    private bool _isProfileDirty
    {
        get => _profileSession.IsDirty;
        set => _profileSession.IsDirty = value;
    }
    private BitmapSource? _capturedImage => _captureSession.CapturedImage;
    private GameWindowInfo? _selectedWindow => _captureSession.SelectedWindow;
    private WgcSelectionResult? _wgcSelection => _captureSession.WgcSelection;
    private InternalTimerOverlayWindow? _internalTimerOverlayWindow;
    private SlotCandidate? _draggingCandidate;
    private Point _candidateDragStartPosition;
    private Dictionary<SlotCandidate, Point> _candidateDragOrigins = new();
    private Rectangle? _selectionRect;
    private Point _selectionStartPosition;
    private bool _isSelectingCandidates;
    private bool _isAwaitingDetectionRoi;
    private bool _isSelectingDetectionRoi;
    private bool _isAwaitingDebugDetectionRoi;
    private bool _isSelectingDebugDetectionRoi;
    private MonitorDetectionMode _monitorDetectionMode;
    private bool _isSelectingMonitorDetectionRoi;
    private bool _isMonitorDetectionBusy;
    private bool _isMonitorValueRecognitionBusy;
    private int _monitorValueRecognitionGeneration;
    private DebugDetectionExpectation _debugDetectionExpectation = DebugDetectionExpectation.TopGrouped1();
    private CandidateEditSnapshot? _candidateDragSnapshotBefore;
    private QuickslotSection? _selectedSection
    {
        get => _workspace.SelectedSection;
        set => _workspace.SelectedSection = value;
    }
    private bool _isUpdatingSectionControls;
    private bool _isUpdatingSectionSelection;
    private bool _isReleasingCaptureIntentionally;
    private int _currentSectionIndex
    {
        get => _workspace.CurrentSectionIndex;
        set => _workspace.CurrentSectionIndex = value;
    }
    private int _nextSectionId
    {
        get => _workspace.NextSectionId;
        set => _workspace.NextSectionId = value;
    }
    private double _layoutCanvasWidth
    {
        get => _workspace.Layout.CanvasWidth;
        set => _workspace.Layout.CanvasWidth = value;
    }
    private double _layoutCanvasHeight
    {
        get => _workspace.Layout.CanvasHeight;
        set => _workspace.Layout.CanvasHeight = value;
    }
    private double _overlayLeft
    {
        get => _workspace.Layout.ScreenLeft;
        set => _workspace.Layout.ScreenLeft = value;
    }
    private double _overlayTop
    {
        get => _workspace.Layout.ScreenTop;
        set => _workspace.Layout.ScreenTop = value;
    }
    private double _overlayOpacity
    {
        get => _workspace.Layout.Opacity;
        set => _workspace.Layout.Opacity = value;
    }
    private string _stopHotkey
    {
        get => _workspace.Layout.StopHotkey;
        set => _workspace.Layout.StopHotkey = value;
    }
    private int _refreshFps
    {
        get => _workspace.Layout.RefreshFps;
        set => _workspace.Layout.RefreshFps = value;
    }
    private double _layoutSlotScale
    {
        get => _workspace.Layout.SlotScale;
        set => _workspace.Layout.SlotScale = value;
    }
    private double _layoutGridSnapSize
    {
        get => _workspace.Layout.GridSnapSize;
        set => _workspace.Layout.GridSnapSize = value;
    }
    private int _alertPreviewRows
    {
        get => _workspace.Layout.AlertPreviewRows;
        set => _workspace.Layout.AlertPreviewRows = Math.Clamp(value, 1, 4);
    }
    private bool _buffMonitorEnabled;
    private bool _tuairimMonitorEnabled;
    private bool _buffAlertsEnabled = true;
    private bool _tuairimAlertsEnabled = true;
    private Rect? _buffMonitorRoi;
    private Rect? _tuairimMonitorRoi;
    private Rect? _tuairimAnchor;
    private DateTimeOffset _nextMonitorValueRecognitionAt;
    private DateTimeOffset _lastInternalTimerCountdownAt;
    private bool _tuairimAlertFired;
    private bool _monitorFrameObscured;
    private int _monitorVisibleRecoveryFrames;
    private bool _isUpdatingMonitorAlertSettings;
    private bool _monitorTestMode;
    private int _monitorTestScenario;
    private bool _monitorTestPreviousBuffEnabled;
    private bool _monitorTestPreviousTuairimEnabled;
    private string[] _monitorTestPreviousRecognizedBuffs = [];
    private string[] _monitorTestPreviousSelectedBuffs = [];
    private double _monitorTestPreviousLayoutCanvasHeight;
    private int _monitorTestTuairimChargeSeconds;
    private int _monitorTestTuairimFullSeconds;
    private string _lastStatusMessage = string.Empty;

    public MainWindow(AppLog? log = null)
    {
        _log = log ?? new AppLog();
        _captureSession = new CaptureSessionCoordinator(_log);
        _overlayRuntime = new OverlayRuntimeController(_captureSession, _log);
        _alertAudio = new AlertAudioService(_log, InternalBuffTimerPreviewRenderer.BuffNameKeys);
        _statusObservations = new StatusObservationController(_log);
        _overlayRuntime.StopRequested += () => Dispatcher.BeginInvoke(() => StopOverlay());
        _overlayRuntime.RuntimeFailed += exception => Dispatcher.BeginInvoke(() =>
        {
            StopOverlay(setStatus: false);
            SetStatus(L.F("Live overlay refresh failed: {0}", exception.Message));
        });
        _candidateWorkspace = new CandidateWorkspace(_workspace);
        _appSettings = _settingsStore.Load();
        if (_settingsStore.LastLoadRecoveredFromBackup)
        {
            _log.Error("App settings were restored from backup because the primary file was invalid.", _settingsStore.LastLoadException!);
        }
        else if (_settingsStore.LastLoadException is not null)
        {
            _log.Error("App settings could not be loaded. Defaults will be used.", _settingsStore.LastLoadException);
        }
        LocalizationService.Instance.SetLanguage(_appSettings.Language);
        _profileStore = new ProfileStore(_appSettings.ProfileDirectory);
        _profileSession = new ProfileSession(_profileStore);
        _detectSessionLogPath = System.IO.Path.Combine(
            _log.LogDirectory,
            $"detect-session-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        InitializeComponent();
        InitializeCustomTimerFeature();
        BuffIconsOnlyCheckBox.IsChecked = _appSettings.BuffIconsOnly;
        ApplyBuffSelectionDisplayMode();
        LocalizationService.Instance.LanguageChanged += LocalizationService_LanguageChanged;
        DataContext = new { Candidates = _candidates };
        SectionCombo.ItemsSource = _sections;
        SectionPatternCombo.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingSectionControls)
            {
                return;
            }

            SaveCurrentSectionSettings();
            _currentSectionIndex = Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1);
            UpdateSectionGapLabels();
            ScheduleProfileAutoSave();
        };
        SmallGapXSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
            ScheduleProfileAutoSave();
        };
        SmallGapYSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
            ScheduleProfileAutoSave();
        };
        LargeGapSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
            ScheduleProfileAutoSave();
        };
        _profileAutoSaveTimer.Tick += (_, _) => FlushProfileAutoSave();
        _internalTimerDebugTimer.Tick += InternalTimerDebugTimer_Tick;
        _inAppNoticeTimer.Tick += (_, _) =>
        {
            _inAppNoticeTimer.Stop();
            InAppNoticeBorder.Visibility = Visibility.Collapsed;
        };
        ErinTimerPanel.AttachLog(_log);
        ErinTimerPanel.NoticeRequested += ShowInAppNotice;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => FlushProfileAutoSave();
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
            ErinTimerPanel.Dispose();
            _alertAudio.Dispose();
            DisposeCustomTimerFeature();
            CloseCompactControlWindow();
            StopOverlay(setStatus: false);
            _overlayRuntime.Dispose();
        };
        Deactivated += (_, _) => CancelInterruptedCaptureInteraction();
        CaptureCanvas.LostMouseCapture += (_, _) => CancelInterruptedCaptureInteraction();
        ApplySectionSettingsToControls(_currentSectionIndex);
        UpdateSizeLabels();
        CaptureZoomText.Text = "100%";
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
        UpdateMonitorControlAvailability();
        RefreshMonitorAlertSettingsControls();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RefreshProfileList();
        LoadStartupAlertSettings();
        _log.Info("Application loaded.");
        if (_appSettings.CompactModeEnabled)
        {
            Dispatcher.BeginInvoke(() => EnterCompactMode(savePreference: false));
        }
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => LocalizationService_LanguageChanged(sender, e));
            return;
        }

        UpdateSizeLabels();
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
        RefreshInternalTimerElementPreviews();
        foreach (var candidate in _candidates.Where(candidate => candidate.IsBuiltIn))
        {
            candidate.RefreshLabel();
        }

        if (WindowStatusText is not null)
        {
            SetWindowStatusText(BuildWindowStatusText());
        }

        if (DetectButton is not null)
        {
            DetectButton.Content = _isAwaitingDetectionRoi ? L.T("Drag ROI...") : L.T("Auto detect section");
        }

        if (DebugDetectButton is not null)
        {
            DebugDetectButton.Content = _isAwaitingDebugDetectionRoi ? L.T("Drag debug ROI...") : L.T("Debug detect");
        }
        UpdateMonitorDetectionButtonPresentation();
        RefreshMonitorAlertSettingsControls();
        UpdateMonitorTestButtonPresentation();

        if (!string.IsNullOrEmpty(_lastStatusMessage) && StatusText is not null)
        {
            StatusText.Text = L.T(_lastStatusMessage);
        }
    }

    private CaptureBackend CurrentCaptureBackend => _appSettings.CaptureBackend;

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowStatusText is not null)
        {
            SetWindowStatusText(BuildWindowStatusText());
        }
    }

    private void CaptureZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CaptureZoomText is not null)
        {
            CaptureZoomText.Text = $"{CaptureZoomSlider.Value * 100:0}%";
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) =>
        CaptureZoomSlider.Value = Math.Min(CaptureZoomSlider.Maximum, CaptureZoomSlider.Value + 0.25);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) =>
        CaptureZoomSlider.Value = Math.Max(CaptureZoomSlider.Minimum, CaptureZoomSlider.Value - 0.25);

    private void SlotSizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSizeLabels();
        ScheduleProfileAutoSave();
    }

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var step = e.Delta > 0 ? 0.25 : -0.25;
        CaptureZoomSlider.Value = Math.Clamp(CaptureZoomSlider.Value + step, CaptureZoomSlider.Minimum, CaptureZoomSlider.Maximum);
        e.Handled = true;
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void AutoCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AutoCaptureButton.IsEnabled = false;
            var result = await _captureSession.CaptureAutoAsync();
            WindowCombo.ItemsSource = result.Windows;
            WindowCombo.SelectedItem = result.Window;
            if (result.Status == CaptureOperationStatus.WindowNotFound)
            {
                SetStatus("Auto capture failed: Mabinogi Client.exe window was not found.");
                _log.Info("Auto WGC capture failed: Mabinogi Client.exe window was not found.");
                return;
            }

            if (result.Status == CaptureOperationStatus.WgcUnavailable)
            {
                SetStatus("Auto capture failed: WGC is not supported.");
                _log.Info("Auto WGC capture failed: WGC is not supported.");
                return;
            }

            var image = _capturedImage!;
            ApplyCapturedPreview(image, L.F("Auto captured WGC Mabinogi window: {0}", result.Window!.DisplayName));
            _log.Info($"Auto WGC capture succeeded: {image.PixelWidth}x{image.PixelHeight}, window={result.Window.DisplayName}");
        }
        catch (Exception ex)
        {
            _log.Error("Auto WGC capture failed.", ex);
            SetStatus(L.F("Auto capture failed: {0}", ex.Message));
        }
        finally
        {
            AutoCaptureButton.IsEnabled = true;
        }
    }

    private async void ManualCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ManualCaptureButton.IsEnabled = false;
            var result = await _captureSession.CaptureManualAsync(this);
            if (result.Status == CaptureOperationStatus.Canceled)
            {
                SetStatus("Manual capture canceled or WGC is not supported.");
                _log.Info("Manual WGC capture picker returned null.");
                return;
            }

            if (result.Status == CaptureOperationStatus.NotMabinogi)
            {
                SetStatus(L.F("Manual capture rejected: selected window is not recognized as Mabinogi ({0}).", result.Selection!.DisplayName));
                _log.Info($"Manual WGC capture rejected: {result.Selection.DisplayName}");
                return;
            }

            WindowCombo.ItemsSource = result.Windows;
            WindowCombo.SelectedItem = result.Window;
            var image = _capturedImage!;
            ApplyCapturedPreview(image, L.F("Manual captured WGC Mabinogi window: {0}", result.Selection!.DisplayName));
            _log.Info($"Manual WGC capture succeeded: {image.PixelWidth}x{image.PixelHeight}, item={result.Selection.DisplayName}");
        }
        catch (Exception ex)
        {
            _log.Error("Manual WGC capture failed.", ex);
            SetStatus(L.F("Manual capture failed: {0}", ex.Message));
        }
        finally
        {
            ManualCaptureButton.IsEnabled = true;
        }
    }

    private void ApplyCapturedPreview(BitmapSource image, string status)
    {
        CaptureImage.Source = image;
        CapturePlaceholder.Visibility = Visibility.Collapsed;
        CaptureCanvas.Width = image.PixelWidth;
        CaptureCanvas.Height = image.PixelHeight;
        CaptureInfoText.Text = $"{image.PixelWidth}x{image.PixelHeight}";
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();
        ClearMonitorDetectionVisuals();
        _buffMonitorRoi = null;
        _buffIconMatches.Clear();
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _pendingInitialBuffMinuteValidation.Clear();
        _tuairimMonitorRoi = null;
        _tuairimAnchor = null;
        ResetTuairimPercentRecognitionState();
        EnsureEnabledMonitorElementsPlaced();
        UpdateMonitorControlAvailability();
        SetStatus(L.F("{0}. Run slot detection next.", status));
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        SetDetectionMode(active: true);
        SetStatus("Drag a quickslot section area on the capture preview, then choose the section type.");
    }

    private void DebugDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var expectation = ChooseDebugDetectionExpectation();
        if (expectation is null)
        {
            SetStatus("Debug detect canceled.");
            return;
        }

        _debugDetectionExpectation = expectation;
        SetDebugDetectionMode(active: true);
        SetStatus(L.F("Debug detect: drag one {0} ROI. It will run 100 simulations and save a log.", expectation.Label));
    }

    private void AddToOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlaceSelectedCandidates(clearExisting: false);
    }

    private void PlaceSelectedCandidates(bool clearExisting)
    {
        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No checked candidates. Check slots to place first.");
            return;
        }

        if (_capturedImage is null && selected.Any(candidate => candidate.Kind == OverlayElementKind.Quickslot))
        {
            SetStatus("Capture and detect candidates first.");
            return;
        }

        if (clearExisting)
        {
            _overlaySlots.Clear();
        }
        else
        {
            selected = selected
                .Where(candidate => _overlaySlots.All(slot => !ReferenceEquals(slot.Source, candidate)))
                .ToList();
            if (selected.Count == 0)
            {
                SetStatus("Selected candidates are already on the overlay.");
                return;
            }
        }

        var cursorX = 8.0;
        var cursorY = clearExisting || _overlaySlots.Count == 0
            ? 8.0
            : _overlaySlots.Max(slot => slot.OverlayRect.Bottom) + 8;
        var rowHeight = 0.0;
        foreach (var candidate in selected)
        {
            if (candidate.IsBuiltIn)
            {
                _hiddenMonitorElementKinds.Remove(candidate.Kind);
            }
            var crop = candidate.Kind == OverlayElementKind.Quickslot
                ? _captureSession.Crop(_capturedImage!, candidate.SourceRect)
                : RenderMonitorElementPreview(candidate.Kind);
            var width = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Width * ReadLayoutSlotScale());
            var height = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Height * ReadLayoutSlotScale());
            if (cursorX + width > _layoutCanvasWidth - 8)
            {
                cursorX = 8;
                cursorY += rowHeight + 8;
                rowHeight = 0;
            }

            var rect = new Rect(cursorX, cursorY, width, height);
            var slot = new OverlaySlot(candidate, rect, crop, scale: ReadLayoutSlotScale());
            _overlaySlots.Add(slot);
            cursorX += width + 8;
            rowHeight = Math.Max(rowHeight, height);
        }

        var requiredHeight = cursorY + rowHeight + 8;
        if (requiredHeight > _layoutCanvasHeight)
        {
            _layoutCanvasHeight = requiredHeight;
        }

        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        ScheduleProfileAutoSave();
        _log.Info($"Placed overlay slots: added={selected.Count}, total={_overlaySlots.Count}, clearExisting={clearExisting}, canvas={_layoutCanvasWidth}x{_layoutCanvasHeight}");
        SetStatus(clearExisting
            ? L.F("Placed {0} slots. Open Manage Layout to arrange them.", _overlaySlots.Count)
            : L.F("Added {0} slot(s) to overlay. Total {1}.", selected.Count, _overlaySlots.Count));
    }

    private void SelectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = true;
        }

        SetStatus(L.F("Selected all {0} candidates.", _candidates.Count));
    }

    private void DeselectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = false;
        }

        SetStatus(L.F("Deselected all {0} candidates.", _candidates.Count));
    }

    private void AddManualCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before adding a manual candidate.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var width = ReadSlotInnerWidth();
        var height = ReadSlotInnerHeight();
        var source = CandidateList.SelectedItem is SlotCandidate selected
            ? new Rect(
                Math.Clamp(selected.SourceRect.X + selected.SourceRect.Width + 4, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Width - width - CandidateBorderPixels)),
                Math.Clamp(selected.SourceRect.Y, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Height - height - CandidateBorderPixels)),
                width,
                height)
            : new Rect(8 + CandidateBorderPixels, 8 + CandidateBorderPixels, width, height);

        var candidate = new SlotCandidate(NextCandidateId(), source, 100);
        AddCandidate(candidate);
        CandidateList.SelectedItem = candidate;
        PushUndoIfChanged(before);
        SetStatus("Manual candidate added. Drag it over the target quickslot.");
    }

    private void ApplySlotSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before resizing a candidate.");
            return;
        }

        var selected = _candidates.Where(candidate => candidate.IsSelected && !candidate.IsBuiltIn).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate { IsBuiltIn: false } highlighted)
        {
            selected.Add(highlighted);
        }

        selected = selected.Distinct().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one candidate before applying slot size.");
            return;
        }

        var candidate = selected[0];
        var width = ReadSlotInnerWidth();
        var height = ReadSlotInnerHeight();
        var resizedRect = new Rect(candidate.SourceRect.X, candidate.SourceRect.Y, width, height);
        if (!IsRectInsideCapture(resizedRect))
        {
            SetStatus("The resized candidate would exceed the captured image bounds.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var oldWidth = Math.Max(1, candidate.SourceRect.Width);
        var oldHeight = Math.Max(1, candidate.SourceRect.Height);
        candidate.ResizeTo(width, height);
        UpdateCandidateVisualPosition(candidate);

        foreach (var slot in _overlaySlots.Where(slot => ReferenceEquals(slot.Source, candidate)))
        {
            slot.Preview = _captureSession.Crop(_capturedImage, candidate.SourceRect);
            var overlayWidth = Math.Max(MinimumOverlaySlotSize, slot.OverlayRect.Width * width / oldWidth);
            var overlayHeight = Math.Max(MinimumOverlaySlotSize, slot.OverlayRect.Height * height / oldHeight);
            slot.OverlayRect = new Rect(slot.OverlayRect.X, slot.OverlayRect.Y, overlayWidth, overlayHeight);
        }

        PushUndoIfChanged(before);
        SetStatus(L.F("Applied candidate size {0}x{1} to #{2}.", width, height, candidate.Id.ToString("000")));
    }

    private void AddSectionFromSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before adding a quickslot section.");
            return;
        }

        var seed = CandidateList.SelectedItem as SlotCandidate ??
                   _candidates.FirstOrDefault(candidate => candidate.IsSelected);
        if (seed is null)
        {
            SetStatus("Select or add the top-left slot of the section first.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var pattern = ReadSectionPattern();
        var settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        var added = AddSectionCandidates(seed, pattern, Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1), settings);
        PushUndoIfChanged(before);
        _log.Info($"Quickslot section generated: pattern={pattern.Name}, seed={seed.Id}, added={added}");
        SetStatus(L.F("Generated {0} from #{1}. Adjust small/large gap sliders to align the section.", L.T(pattern.Name), seed.Id.ToString("000")));
    }

    private void DeleteSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSection is null)
        {
            SetStatus("No section is selected.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var candidates = _selectedSection.Candidates.Where(candidate => _candidates.Contains(candidate)).ToList();
        foreach (var candidate in candidates)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }

            _candidates.Remove(candidate);
        }

        RemoveOverlaySlotsForCandidates(candidates);
        _sections.Remove(_selectedSection);
        _selectedSection = null;
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus(L.F("Deleted section and {0} candidate(s).", candidates.Count));
    }

    private void SectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSectionSelection)
        {
            return;
        }

        _selectedSection = SectionCombo.SelectedItem as QuickslotSection;
        if (_selectedSection is null)
        {
            return;
        }

        SelectSectionCandidates(_selectedSection);
        LoadSectionControls(_selectedSection);
        SetStatus(L.F("Selected section {0}.", _selectedSection.Label));
    }

    private void DeleteSelectedCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedCandidates();
    }

    private void CandidateList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleUndoRedo(e))
        {
            return;
        }

        if (e.Key != Key.Delete)
        {
            if (TryNudgeSelectedCandidates(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        DeleteSelectedCandidates();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        if (TryHandleCandidateNudgeKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void PreviewScrollViewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleCandidateNudgeKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox && TryHandleUndoRedo(e))
        {
            return;
        }

        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
            if (e.OriginalSource is not TextBox && TryNudgeSelectedCandidates(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        DeleteSelectedCandidates();
        e.Handled = true;
    }

    private bool TryHandleCandidateNudgeKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down && TryNudgeSelectedCandidates(key);

    private void DeleteSelectedCandidates()
    {
        var selected = GetCandidatesToDelete();
        if (selected.Count == 0)
        {
            SetStatus("No selected candidates to delete.");
            return;
        }

        var builtInCandidates = selected.Where(candidate => candidate.IsBuiltIn).ToList();
        var removableCandidates = selected.Where(candidate => !candidate.IsBuiltIn).ToList();
        var before = CaptureCandidateSnapshot();
        foreach (var candidate in removableCandidates)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }
        }

        _candidateWorkspace.DeleteCandidates(removableCandidates);
        var hiddenSlots = 0;
        foreach (var candidate in builtInCandidates)
        {
            hiddenSlots += _overlaySlots.RemoveAll(slot => slot.Kind == candidate.Kind);
            _hiddenMonitorElementKinds.Add(candidate.Kind);
        }

        RefreshSectionLabels();
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        RefreshMonitorDisplayControls();
        RefreshCustomTimerEditor();
        PushUndoIfChanged(before);
        ScheduleProfileAutoSave();
        SetStatus(builtInCandidates.Count > 0
            ? L.F("profile.monitor.slots.hidden", hiddenSlots)
            : L.F("Deleted {0} selected candidates.", removableCandidates.Count));
    }

    private void CandidateList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        CandidateList.SelectedItem = null;
        Keyboard.ClearFocus();
    }

    private List<SlotCandidate> GetCandidatesToDelete()
    {
        var checkedCandidates = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (checkedCandidates.Count > 0)
        {
            return checkedCandidates;
        }

        return CandidateList.SelectedItem is SlotCandidate highlighted
            ? [highlighted]
            : [];
    }

    private void ClearCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        var removableCandidates = _candidates.Where(candidate => !candidate.IsBuiltIn).ToList();
        if (removableCandidates.Count == 0 && _overlaySlots.Count == 0)
        {
            SetStatus("candidate.list.is.already.empty");
            return;
        }

        SlotResetConfirmationOverlay.Visibility = Visibility.Visible;
        ConfirmSlotResetButton.Focus();
    }

    private void CancelSlotResetButton_Click(object sender, RoutedEventArgs e)
    {
        SlotResetConfirmationOverlay.Visibility = Visibility.Collapsed;
        SetStatus("clear.canceled");
    }

    private void ConfirmSlotResetButton_Click(object sender, RoutedEventArgs e)
    {
        SlotResetConfirmationOverlay.Visibility = Visibility.Collapsed;

        var removableCandidates = _candidates.Where(candidate => !candidate.IsBuiltIn).ToList();
        var before = CaptureCandidateSnapshot();
        foreach (var candidate in removableCandidates)
        {
            _candidates.Remove(candidate);
        }
        _overlaySlots.Clear();
        HideAllMonitorElements();
        ClearCandidateRects();
        ClearSections();
        UpdateCandidateOverlayFlags();
        RefreshMonitorDisplayControls();
        RefreshCustomTimerEditor();
        PushUndoIfChanged(before);
        ScheduleProfileAutoSave();
        SetStatus("slot.reset.completed");
    }

    private void OpenLayoutEditorButton_Click(object sender, RoutedEventArgs e) => OpenLayoutEditor(this);

    private void OpenLayoutEditor(Window owner)
    {
        if (_overlayRuntime.IsRunning)
        {
            ShowInAppNotice(L.T("Stop the overlay before opening Manage Layout."));
            SetStatus("Stop the overlay before opening Manage Layout.");
            return;
        }

        var editor = new LayoutEditorWindow(
            _layoutCanvasWidth,
            _layoutCanvasHeight,
            _overlayLeft,
            _overlayTop,
            _overlayOpacity,
            _stopHotkey,
            _refreshFps,
            _layoutSlotScale,
            _layoutGridSnapSize,
            _alertPreviewRows,
            _overlaySlots)
        {
            Owner = owner
        };
        void ApplyEditorState()
        {
            _layoutCanvasWidth = editor.CanvasWidth;
            _layoutCanvasHeight = editor.CanvasHeight;
            _overlayLeft = editor.ScreenLeft;
            _overlayTop = editor.ScreenTop;
            _overlayOpacity = editor.OverlayOpacity;
            _stopHotkey = editor.StopHotkey;
            _refreshFps = editor.RefreshFps;
            _layoutSlotScale = editor.SlotScale;
            _layoutGridSnapSize = editor.GridSnapSize;
            _alertPreviewRows = editor.AlertPreviewRows;
            SynchronizeMonitorElementDimensions();
            RefreshInternalTimerElementPreviews();
            SynchronizeHiddenMonitorElementsFromLayout();
            EnsureEnabledMonitorElementsPlaced();
            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
            RefreshMonitorDisplayControls();
            RefreshCustomTimerEditor();
        }

        if (editor.ShowDialog() == true)
        {
            ApplyEditorState();
            ScheduleProfileAutoSave();
            FlushProfileAutoSave();
            SetStatus("Layout editor closed. Overlay settings updated.");
            return;
        }

        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        SetStatus("layout.editing.canceled");
    }

    private void ClearLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("overlay.layout.is.already.empty");
            return;
        }

        LayoutResetConfirmationOverlay.Visibility = Visibility.Visible;
        ConfirmLayoutResetButton.Focus();
    }

    private void CancelLayoutResetButton_Click(object sender, RoutedEventArgs e)
    {
        LayoutResetConfirmationOverlay.Visibility = Visibility.Collapsed;
        SetStatus("clear.canceled");
    }

    private void ConfirmLayoutResetButton_Click(object sender, RoutedEventArgs e)
    {
        LayoutResetConfirmationOverlay.Visibility = Visibility.Collapsed;
        ClearLayout();
        ScheduleProfileAutoSave();
        SetStatus("overlay.layout.cleared");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _profileStore.ProfileDirectory,
            _settingsStore.DefaultProfileDirectory,
            _appSettings.OverlayRenderMode,
            _appSettings.AutomaticRendererSelection,
            _appSettings.CaptureBackend,
            _appSettings.Language,
            _log.LogPath,
            _log.SessionStartedAt)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            SetStatus("Settings canceled.");
            return;
        }

        try
        {
            FlushProfileAutoSave();
            var directory = _settingsStore.NormalizeProfileDirectory(dialog.ProfileDirectory);
            System.IO.Directory.CreateDirectory(directory);
            _appSettings.ProfileDirectory = directory;
            _appSettings.OverlayRenderMode = dialog.SelectedRenderMode;
            _appSettings.AutomaticRendererSelection = dialog.AutomaticRendererSelection;
            _appSettings.CaptureBackend = dialog.SelectedCaptureBackend;
            _appSettings.Language = LocalizationService.NormalizeLanguage(dialog.SelectedLanguage);
            LocalizationService.Instance.SetLanguage(_appSettings.Language);
            _settingsStore.Save(_appSettings);
            _profileStore.SetProfileDirectory(directory);
            RefreshProfileList(ReadSelectedProfileName());
            SetWindowStatusText(BuildWindowStatusText());
            _log.Info(
                $"Settings saved: profileDirectory={directory}, renderMode={_appSettings.OverlayRenderMode}, " +
                $"automaticRenderer={_appSettings.AutomaticRendererSelection}, captureBackend={_appSettings.CaptureBackend}");
        }
        catch (Exception exception)
        {
            _log.Error("Failed to save settings.", exception);
            SetStatus(L.F("Settings save failed: {0}", exception.Message));
            return;
        }

        ScheduleProfileAutoSave();
        var rendererStatus = _appSettings.AutomaticRendererSelection
            ? L.F(
                "renderer.automatic.active.arg",
                L.T(UserRenderModeLabel(_appSettings.OverlayRenderMode)))
            : L.T(UserRenderModeLabel(_appSettings.OverlayRenderMode));
        SetStatus(L.F(
            "Settings saved: {0}, renderer={1}, capture={2}",
            _profileStore.ProfileDirectory,
            rendererStatus,
            L.T(CaptureBackendLabel(_appSettings.CaptureBackend))));
    }

    private void DebugTabToggle_Click(object sender, RoutedEventArgs e) =>
        RightPanelTabs.SelectedItem = DebugTabItem;

    private void ShowInAppNotice(string message)
    {
        InAppNoticeText.Text = message;
        InAppNoticeBorder.Visibility = Visibility.Visible;
        _inAppNoticeTimer.Stop();
        _inAppNoticeTimer.Start();
    }




    private async void StartOverlayButton_Click(object sender, RoutedEventArgs e) => await StartOverlayAsync();

    private async Task StartOverlayAsync()
    {
        try
        {
            CommitCustomTimerEditor();
            var result = await _overlayRuntime.StartAsync(
                this,
                new OverlayRuntimeOptions(
                    _overlaySlots,
                    _workspace.Layout,
                    CurrentCaptureBackend,
                    _appSettings.OverlayRenderMode,
                    _buffMonitorEnabled,
                    _tuairimMonitorEnabled,
                    _selectedBuffNameKeys.Count > 0,
                    _monitorTestMode,
                    _customTimerDefinitions.Select(timer => timer.Clone()).ToArray()));
            switch (result.Status)
            {
                case OverlayRuntimeStartStatus.AlreadyRunning:
                    return;
                case OverlayRuntimeStartStatus.NoRenderableElements:
                    SetStatus("No slots are placed on the overlay canvas.");
                    return;
                case OverlayRuntimeStartStatus.MissingCaptureSource:
                    SetStatus(L.F(
                        "Run Auto capture or Manual capture before starting the overlay with {0}.",
                        L.T(CaptureBackendLabel(CurrentCaptureBackend))));
                    return;
                case OverlayRuntimeStartStatus.InvalidHotkey:
                    SetStatus("Invalid hotkey. Use a format like Ctrl+Shift+F8.");
                    return;
                case OverlayRuntimeStartStatus.HotkeyRegistrationFailed:
                    SetStatus(L.F("Stop hotkey registration failed: {0}", _stopHotkey));
                    return;
                case OverlayRuntimeStartStatus.InvalidCustomTimerHotkey:
                    SetStatus(L.F("custom.timer.hotkey.invalid", result.Detail ?? string.Empty));
                    return;
                case OverlayRuntimeStartStatus.DuplicateCustomTimerHotkey:
                    SetStatus(L.F("custom.timer.hotkey.duplicate", result.Detail ?? string.Empty));
                    return;
                case OverlayRuntimeStartStatus.CustomTimerHotkeyRegistrationFailed:
                    SetStatus(L.F("custom.timer.hotkey.registration.failed", result.Detail ?? string.Empty));
                    return;
                case OverlayRuntimeStartStatus.ClickThroughConfigurationFailed:
                    SetStatus(L.F("Overlay click-through configuration failed: {0}", result.Detail ?? string.Empty));
                    return;
                case OverlayRuntimeStartStatus.Failed:
                    SetStatus(L.F("Overlay start failed: {0}", result.Detail ?? string.Empty));
                    return;
                case OverlayRuntimeStartStatus.Success:
                    StartInternalTimerOverlay();
                    UpdateMonitorControlAvailability();
                    UpdateCustomTimerControlAvailability();
                    SetStatus(L.F(
                        "Overlay started ({0}, {1}, {2}). Stop hotkey: {3}",
                        L.T(result.ClickThroughStatus!),
                        L.T(CaptureBackendLabel(CurrentCaptureBackend)),
                        L.T(result.RendererMode!),
                        _stopHotkey));
                    return;
            }
        }
        finally
        {
            RefreshCompactControlState();
        }
    }

    private void StopOverlayButton_Click(object sender, RoutedEventArgs e) => StopOverlay();

    private void TitleBarArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMainWindowMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse state changes while the drag starts.
        }
    }

    private void CandidateLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotCandidate candidate })
        {
            return;
        }

        CandidateList.SelectedItem = candidate;
        if (e.ClickCount != 2)
        {
            return;
        }

        candidate.IsSelected = !candidate.IsSelected;
        e.Handled = true;
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMainWindowMaximize();

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMainWindowStateButton();

    private void ToggleMainWindowMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMainWindowStateButton()
    {
        if (MaximizeWindowIcon is not null)
        {
            MaximizeWindowIcon.Text = WindowState == WindowState.Maximized ? char.ConvertFromUtf32(0x1F5D7) : char.ConvertFromUtf32(0x1F5D6);
        }
    }




    private void StopOverlay(bool setStatus = true)
    {
        _overlayRuntime.Stop();
        StopCustomTimers();
        _internalTimerDebugTimer.Stop();
        _monitorRecognitionRetryPolicy.Reset();
        _pendingInitialBuffMinuteValidation.Clear();
        ResetTuairimPercentRecognitionState();
        _monitorValueRecognitionGeneration++;
        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = null;
        UpdateMonitorControlAvailability();
        UpdateCustomTimerControlAvailability();
        if (setStatus)
        {
            _log.Info("Overlay stopped.");
            SetStatus("Overlay stopped.");
        }
        RefreshCompactControlState();
    }

    private int ReadSlotInnerWidth() => ReadSlotDimension(SlotWidthBox?.Text, 29);

    private int ReadSlotInnerHeight() => ReadSlotDimension(SlotHeightBox?.Text, 29);

    private static int ReadSlotDimension(string? text, int fallback) =>
        int.TryParse(text, out var value)
            ? Math.Clamp(value, 1, 300)
            : Math.Clamp(fallback, 1, 300);

    private static string ReadSlotDimensionText(int value, int fallback) =>
        Math.Clamp(value > 0 ? value : fallback, 1, 300).ToString();

    private int ReadCandidateBoxWidth() => ReadSlotInnerWidth() + CandidateBorderPixels * 2;

    private int ReadCandidateBoxHeight() => ReadSlotInnerHeight() + CandidateBorderPixels * 2;

    private double ReadLayoutSlotScale() => Math.Clamp(_layoutSlotScale, 0.1, 10);

    private static T? FindVisualAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static double InferSlotScale(OverlayProfileSlot slot)
    {
        var scaleX = slot.SourceWidth > 0 ? slot.OverlayWidth / slot.SourceWidth : 1;
        var scaleY = slot.SourceHeight > 0 ? slot.OverlayHeight / slot.SourceHeight : 1;
        var scale = Math.Min(scaleX > 0 ? scaleX : 1, scaleY > 0 ? scaleY : 1);
        return Math.Clamp(scale, 0.1, 10);
    }

    private static SlotCandidate? ResolveProfileSlotSource(
        OverlayProfileSlot slot,
        IReadOnlyDictionary<int, SlotCandidate> candidates)
    {
        if (slot.SourceCandidateId > 0 && candidates.TryGetValue(slot.SourceCandidateId, out var byId))
        {
            return byId;
        }

        return candidates.Values.FirstOrDefault(candidate =>
            Math.Abs(candidate.SourceRect.X - slot.SourceX) < 0.001 &&
            Math.Abs(candidate.SourceRect.Y - slot.SourceY) < 0.001 &&
            Math.Abs(candidate.SourceRect.Width - slot.SourceWidth) < 0.001 &&
            Math.Abs(candidate.SourceRect.Height - slot.SourceHeight) < 0.001);
    }

    private double ReadSmallGapX() => Math.Clamp(SmallGapXSlider.Value, 2, 30);

    private double ReadSmallGapY() => Math.Clamp(SmallGapYSlider.Value, 2, 30);

    private double ReadLargeGap() => Math.Clamp(LargeGapSlider.Value, 2, 60);

    private static int CoerceRefreshFps(int fps) =>
        RefreshFpsOptions.OrderBy(option => Math.Abs(option - fps)).First();

    private static int RefreshIntervalFromFps(int fps) =>
        (int)Math.Max(1, Math.Round(1000.0 / CoerceRefreshFps(fps)));

    private static string RenderModeLabel(OverlayRenderMode mode) =>
        mode switch
        {
            OverlayRenderMode.GpuDxgi => "GPU/DXGI",
            OverlayRenderMode.CpuComposited => "CPU/Composited",
            _ => "CPU/WPF"
        };

    private static string UserRenderModeLabel(OverlayRenderMode mode) =>
        mode switch
        {
            OverlayRenderMode.GpuDxgi => "renderer.gpu.accelerated",
            OverlayRenderMode.CpuComposited => "renderer.cpu.composited",
            _ => "renderer.wpf.compatibility"
        };

    private static string CaptureBackendLabel(CaptureBackend backend) =>
        backend switch
        {
            CaptureBackend.DxgiDesktopDuplication => "DXGI Desktop Duplication",
            CaptureBackend.GdiBitBlt => "GDI BitBlt",
            _ => "WGC"
        };

    private static int FpsFromInterval(int intervalMs) =>
        intervalMs <= 0 ? 60 : (int)Math.Round(1000.0 / intervalMs);

    private void UpdateSizeLabels()
    {
        if (SlotSizeText is not null)
        {
            SlotSizeText.Text = L.F(
                "inside {0}x{1}px, box {2}x{3}px",
                ReadSlotInnerWidth(),
                ReadSlotInnerHeight(),
                ReadCandidateBoxWidth(),
                ReadCandidateBoxHeight());
        }
    }

    private void UpdateSectionGapLabels()
    {
        var usesLargeGap = SectionPatternCombo.SelectedIndex == 0;
        SmallGapXText.Text = L.F("Small gap X {0}px", ReadSmallGapX().ToString("0"));
        SmallGapYText.Text = L.F("Small gap Y {0}px", ReadSmallGapY().ToString("0"));
        LargeGapText.Text = usesLargeGap
            ? L.F("Large gap {0}px", ReadLargeGap().ToString("0"))
            : L.T("Large gap unused");
        LargeGapSlider.IsEnabled = usesLargeGap;
    }

    private void SaveCurrentSectionSettings()
    {
        if (_isUpdatingSectionControls)
        {
            return;
        }

        var index = Math.Clamp(_currentSectionIndex, 0, _sectionSettings.Length - 1);
        var settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        _sectionSettings[index] = settings;
        if (_selectedSection is not null && _selectedSection.PatternIndex == index)
        {
            _selectedSection.Settings = settings;
        }
    }

    private void ApplySectionSettingsToControls(int patternIndex)
    {
        _isUpdatingSectionControls = true;
        try
        {
            var settings = _sectionSettings[Math.Clamp(patternIndex, 0, _sectionSettings.Length - 1)];
            SmallGapXSlider.Value = Math.Clamp(settings.SmallGapX, SmallGapXSlider.Minimum, SmallGapXSlider.Maximum);
            SmallGapYSlider.Value = Math.Clamp(settings.SmallGapY, SmallGapYSlider.Minimum, SmallGapYSlider.Maximum);
            LargeGapSlider.Value = Math.Clamp(settings.LargeGap, LargeGapSlider.Minimum, LargeGapSlider.Maximum);
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }
    }

    private void ApplyProfileSectionSettingsToControls()
    {
        _isUpdatingSectionControls = true;
        try
        {
            SectionPatternCombo.SelectedIndex = _currentSectionIndex;
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }

        ApplySectionSettingsToControls(_currentSectionIndex);
        UpdateSectionGapLabels();
    }

    private string ReadSelectedProfileName() =>
        string.IsNullOrWhiteSpace(_selectedProfileName) ? "default" : _selectedProfileName;

    private string ReadProfileComboName() =>
        ProfileCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected)
            ? selected.Trim()
            : ReadSelectedProfileName();

    private void RefreshProfileList(string? selectedProfileName = null)
    {
        _profileSession.RefreshProfileNames(selectedProfileName);
        _isUpdatingProfileSelection = true;
        try
        {
            ProfileCombo.ItemsSource = null;
            ProfileCombo.ItemsSource = _profileNames;
            ProfileCombo.SelectedItem = _selectedProfileName;
        }
        finally
        {
            _isUpdatingProfileSelection = false;
        }
    }

    private static string GetSectionPatternName(int index) => SectionPattern.NameFor(index);

    private void UpdateLayoutSummary()
    {
        LayoutSummaryText.Text = string.Join(
            " | ",
            L.F("Slots: {0}", _overlaySlots.Count),
            L.F("Canvas: {0}x{1}", _layoutCanvasWidth.ToString("0"), _layoutCanvasHeight.ToString("0")),
            L.F("Screen: {0}, {1}", _overlayLeft.ToString("0"), _overlayTop.ToString("0")),
            L.F("Opacity {0}%", (_overlayOpacity * 100).ToString("0")),
            L.F("Scale: {0}x", _layoutSlotScale.ToString("0.0")),
            L.F("Grid: {0}px", _layoutGridSnapSize.ToString("0")),
            L.F("Hotkey: {0}", _stopHotkey),
            L.F("Max FPS: {0}", _refreshFps));
    }

    private void SetStatus(string message)
    {
        _lastStatusMessage = message;
        StatusText.Text = L.T(message);
        _log.Info($"Status: {message}");
    }

    private enum MonitorDetectionMode
    {
        None,
        BuffWindow,
        Tuairim
    }

}
