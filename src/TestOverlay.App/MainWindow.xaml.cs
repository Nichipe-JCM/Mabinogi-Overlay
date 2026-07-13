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
    private const string BuffSoundModeGlobal = "global";
    private const string BuffSoundModeIndividual = "individual";
    private const string TuairimAlertFrequencyOnce = "once";
    private const string TuairimAlertFrequencyEveryPercent = "every-percent";
    private const int TuairimNormalChargeSecondsPerPercent = 6;
    private const int TuairimFullEffectSeconds = 20;
    private static readonly Color ProjectAccentColor = Color.FromRgb(0x89, 0xDE, 0xD4);
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly CaptureSessionCoordinator _captureSession;
    private readonly RoiSectionDetectionService _roiSectionDetection = new();
    private readonly CpuCompositedOverlayRenderer _cpuCompositedRenderer = new();
    private readonly MonitorTemplateDetectionService _monitorTemplateDetection = new();
    private readonly MonitorValueRecognitionService _monitorValueRecognition = new();
    private readonly MediaPlayer _buffAlertPlayer = new();
    private readonly Dictionary<string, MediaPlayer> _individualBuffAlertPlayers = new(StringComparer.Ordinal);
    private readonly MediaPlayer _tuairimAlertPlayer = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ProfileStore _profileStore;
    private readonly ProfileSession _profileSession;
    private AppSettings _appSettings;
    private readonly AppLog _log = new();
    private readonly object _detectLogSync = new();
    private readonly string _detectSessionLogPath;
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _profileAutoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _internalTimerDebugTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _inAppNoticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly OverlayWorkspaceState _workspace = new();
    private readonly CandidateWorkspace _candidateWorkspace;
    private ObservableCollection<SlotCandidate> _candidates => _workspace.Candidates;
    private ObservableCollection<QuickslotSection> _sections => _workspace.Sections;
    private List<OverlaySlot> _overlaySlots => _workspace.OverlaySlots;
    private readonly List<InternalBuffTimer> _internalBuffTimers = new();
    private readonly HashSet<string> _recognizedBuffNameKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedBuffNameKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingInitialBuffMinuteValidation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _buffAlertSoundPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _buffAlertVolumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuffIconMatch> _buffIconMatches = new(StringComparer.Ordinal);
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
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage => _captureSession.CapturedImage;
    private GameWindowInfo? _selectedWindow => _captureSession.SelectedWindow;
    private WgcSelectionResult? _wgcSelection => _captureSession.WgcSelection;
    private OverlayWindow? _overlayWindow;
    private InternalTimerOverlayWindow? _internalTimerOverlayWindow;
    private GpuLiveOverlayService? _gpuLiveOverlayService;
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
    private bool _isLiveRefreshInProgress;
    private bool _isUpdatingSectionControls;
    private bool _isUpdatingSectionSelection;
    private bool _isReleasingCaptureIntentionally;
    private readonly Stopwatch _cpuRenderClock = new();
    private OverlayRenderMode _activeRenderMode = OverlayRenderMode.CpuWpf;
    private long _cpuStatsLastLogTicks;
    private long _cpuStatsTicks;
    private long _cpuStatsMaxTicks;
    private int _cpuStatsFrames;
    private int _cpuStatsSkippedBusy;
    private int _cpuStatsErrors;
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
    private bool _buffMonitorEnabled;
    private bool _tuairimMonitorEnabled;
    private Rect? _buffMonitorRoi;
    private Rect? _tuairimMonitorRoi;
    private Rect? _tuairimAnchor;
    private DateTimeOffset _nextMonitorValueRecognitionAt;
    private int _tuairimPercent;
    private bool _hasTuairimPercentObservation;
    private int? _pendingTuairimPercent;
    private int _pendingTuairimPercentConfirmations;
    private DateTimeOffset? _lastAcceptedTuairimPercentAt;
    private int _buffAlertSeconds = 30;
    private string _buffAlertSoundPath = string.Empty;
    private int _buffAlertVolume = 100;
    private string _buffAlertSoundMode = BuffSoundModeGlobal;
    private int _tuairimAlertPercent = 95;
    private string _tuairimAlertSoundPath = string.Empty;
    private int _tuairimAlertVolume = 100;
    private string _tuairimAlertFrequency = TuairimAlertFrequencyOnce;
    private bool _tuairimAlertFired;
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

    public MainWindow()
    {
        _captureSession = new CaptureSessionCoordinator(_log);
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
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        _profileAutoSaveTimer.Tick += (_, _) => FlushProfileAutoSave();
        _internalTimerDebugTimer.Tick += InternalTimerDebugTimer_Tick;
        _inAppNoticeTimer.Tick += (_, _) =>
        {
            _inAppNoticeTimer.Stop();
            InAppNoticeBorder.Visibility = Visibility.Collapsed;
        };
        ErinTimerPanel.AttachLog(_log);
        ErinTimerPanel.NoticeRequested += ShowInAppNotice;
        _buffAlertPlayer.MediaFailed += (_, args) => _log.Error("Buff alert media playback failed.", args.ErrorException);
        foreach (var nameKey in InternalBuffTimerPreviewRenderer.BuffNameKeys)
        {
            var player = new MediaPlayer();
            player.MediaFailed += (_, args) => _log.Error($"Buff alert media playback failed: key={nameKey}.", args.ErrorException);
            _individualBuffAlertPlayers[nameKey] = player;
        }
        _tuairimAlertPlayer.MediaFailed += (_, args) => _log.Error("Tuairim alert media playback failed.", args.ErrorException);
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => FlushProfileAutoSave();
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
            ErinTimerPanel.Dispose();
            CloseAlertPlayers();
            StopOverlay(setStatus: false);
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

        var before = CaptureCandidateSnapshot();
        foreach (var candidate in selected)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }
        }

        _candidateWorkspace.DeleteCandidates(selected);

        RefreshSectionLabels();
        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus(L.F("Deleted {0} selected candidates.", selected.Count));
    }

    private List<SlotCandidate> GetCandidatesToDelete()
    {
        var checkedCandidates = _candidates.Where(candidate => candidate.IsSelected && !candidate.IsBuiltIn).ToList();
        if (checkedCandidates.Count > 0)
        {
            return checkedCandidates;
        }

        return CandidateList.SelectedItem is SlotCandidate { IsBuiltIn: false } highlighted
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

        if (MessageBox.Show(
                this,
                L.T("clear.candidates.confirm"),
                L.T("confirm.clear"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("clear.canceled");
            return;
        }

        var before = CaptureCandidateSnapshot();
        foreach (var candidate in removableCandidates)
        {
            _candidates.Remove(candidate);
        }
        _overlaySlots.Clear();
        ClearCandidateRects();
        ClearSections();
        EnsureEnabledMonitorElementsPlaced();
        UpdateCandidateOverlayFlags();
        PushUndoIfChanged(before);
        SetStatus("Candidate list cleared.");
    }

    private void OpenLayoutEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow is not null)
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
            _overlaySlots)
        {
            Owner = this
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
            EnsureEnabledMonitorElementsPlaced();
            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
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

        if (MessageBox.Show(
                this,
                L.T("clear.layout.confirm"),
                L.T("confirm.clear"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("clear.canceled");
            return;
        }

        ClearLayout();
        ScheduleProfileAutoSave();
        SetStatus("Overlay layout cleared.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _profileStore.ProfileDirectory,
            _settingsStore.DefaultProfileDirectory,
            _appSettings.OverlayRenderMode,
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
            _appSettings.CaptureBackend = dialog.SelectedCaptureBackend;
            _appSettings.Language = LocalizationService.NormalizeLanguage(dialog.SelectedLanguage);
            LocalizationService.Instance.SetLanguage(_appSettings.Language);
            _settingsStore.Save(_appSettings);
            _profileStore.SetProfileDirectory(directory);
            RefreshProfileList(ReadSelectedProfileName());
            SetWindowStatusText(BuildWindowStatusText());
            _log.Info($"Settings saved: profileDirectory={directory}, renderMode={_appSettings.OverlayRenderMode}, captureBackend={_appSettings.CaptureBackend}");
        }
        catch (Exception exception)
        {
            _log.Error("Failed to save settings.", exception);
            SetStatus(L.F("Settings save failed: {0}", exception.Message));
            return;
        }

        ScheduleProfileAutoSave();
        SetStatus(L.F(
            "Settings saved: {0}, renderer={1}, capture={2}",
            _profileStore.ProfileDirectory,
            L.T(RenderModeLabel(_appSettings.OverlayRenderMode)),
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

    private void BuffMonitorEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _buffMonitorEnabled = BuffMonitorEnabledCheckBox.IsChecked == true;
        if (!_buffMonitorEnabled)
        {
            if (_monitorDetectionMode == MonitorDetectionMode.BuffWindow)
            {
                SetMonitorDetectionMode(MonitorDetectionMode.None);
            }
            _recognizedBuffNameKeys.Clear();
            _selectedBuffNameKeys.Clear();
            _pendingInitialBuffMinuteValidation.Clear();
            _buffMonitorRoi = null;
            _buffIconMatches.Clear();
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, _buffMonitorEnabled);
        SynchronizeAlertNotificationElement();
        UpdateMonitorControlAvailability();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        if (_buffMonitorEnabled)
        {
            DetectBuffWindowButton_Click(DetectBuffWindowButton, new RoutedEventArgs());
        }
    }

    private void TuairimMonitorEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _tuairimMonitorEnabled = TuairimMonitorEnabledCheckBox.IsChecked == true;
        if (!_tuairimMonitorEnabled && _monitorDetectionMode == MonitorDetectionMode.Tuairim)
        {
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }
        if (!_tuairimMonitorEnabled)
        {
            _tuairimMonitorRoi = null;
            _tuairimAnchor = null;
            ResetTuairimPercentRecognitionState();
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, _tuairimMonitorEnabled);
        SynchronizeAlertNotificationElement();
        UpdateMonitorControlAvailability();
        RefreshInternalTimerOverlay();
        if (_tuairimMonitorEnabled)
        {
            DetectTuairimUiButton_Click(DetectTuairimUiButton, new RoutedEventArgs());
        }
    }

    private void MonitorTestModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTestMode && _monitorTestScenario == 1)
        {
            ExitMonitorTestMode();
        }
        else
        {
            SwitchMonitorTestMode(1);
        }
    }

    private void MonitorTestMode2Button_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTestMode && _monitorTestScenario == 2)
        {
            ExitMonitorTestMode();
        }
        else
        {
            SwitchMonitorTestMode(2);
        }
    }

    private void SwitchMonitorTestMode(int scenario)
    {
        if (_monitorTestMode)
        {
            ExitMonitorTestMode();
        }
        EnterMonitorTestMode(scenario);
    }

    private void EnterMonitorTestMode(int scenario)
    {
        FlushProfileAutoSave();
        _monitorTestPreviousBuffEnabled = _buffMonitorEnabled;
        _monitorTestPreviousTuairimEnabled = _tuairimMonitorEnabled;
        _monitorTestPreviousRecognizedBuffs = _recognizedBuffNameKeys.ToArray();
        _monitorTestPreviousSelectedBuffs = _selectedBuffNameKeys.ToArray();
        _monitorTestPreviousLayoutCanvasHeight = _layoutCanvasHeight;
        _monitorTestMode = true;
        _monitorTestScenario = scenario;
        _monitorValueRecognitionGeneration++;
        SetMonitorDetectionMode(MonitorDetectionMode.None);

        _buffMonitorEnabled = true;
        _tuairimMonitorEnabled = true;
        BuffMonitorEnabledCheckBox.IsChecked = true;
        TuairimMonitorEnabledCheckBox.IsChecked = true;
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _recognizedBuffNameKeys.Add("monitor.buff.battle.overture");
        _recognizedBuffNameKeys.Add("monitor.buff.march.song");
        _selectedBuffNameKeys.Add("monitor.buff.battle.overture");
        _selectedBuffNameKeys.Add("monitor.buff.march.song");

        _internalBuffTimers.Clear();
        var battleSeconds = scenario == 2 ? 32 : 35;
        var marchSeconds = scenario == 2 ? 34 : 40;
        _internalBuffTimers.Add(new InternalBuffTimer("monitor.buff.battle.overture", battleSeconds));
        _internalBuffTimers.Add(new InternalBuffTimer("monitor.buff.march.song", marchSeconds) { HasHarmony = true });
        _tuairimPercent = scenario == 2 ? 89 : 88;
        _hasTuairimPercentObservation = true;
        _lastAcceptedTuairimPercentAt = DateTimeOffset.UtcNow;
        _tuairimAlertFired = false;
        _monitorTestTuairimChargeSeconds = scenario == 2 ? TuairimNormalChargeSecondsPerPercent - 1 : 0;
        _monitorTestTuairimFullSeconds = 0;

        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, enabled: true, scheduleAutoSave: false);
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, enabled: true, scheduleAutoSave: false);
        SynchronizeAlertNotificationElement(scheduleAutoSave: false);
        UpdateBuffSelectionCheckStates();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        _internalTimerOverlayWindow?.SetTuairimPercent(_tuairimPercent);
        if (_overlayWindow is not null)
        {
            _internalTimerDebugTimer.Start();
        }
        UpdateMonitorControlAvailability();
        UpdateMonitorTestButtonPresentation();
        _log.Info(
            $"Monitor test mode {scenario} started: Battle Overture={battleSeconds}s, " +
            $"March Song[Harmony]={marchSeconds}s, Tuairim={_tuairimPercent}%.");
        SetStatus("monitor.test.started");
    }

    private void ExitMonitorTestMode()
    {
        _monitorTestMode = false;
        _monitorTestScenario = 0;
        _monitorValueRecognitionGeneration++;
        _internalBuffTimers.Clear();
        ResetTuairimPercentRecognitionState();
        _tuairimPercent = 0;
        _monitorTestTuairimChargeSeconds = 0;
        _monitorTestTuairimFullSeconds = 0;

        _buffMonitorEnabled = _monitorTestPreviousBuffEnabled;
        _tuairimMonitorEnabled = _monitorTestPreviousTuairimEnabled;
        BuffMonitorEnabledCheckBox.IsChecked = _buffMonitorEnabled;
        TuairimMonitorEnabledCheckBox.IsChecked = _tuairimMonitorEnabled;
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        foreach (var nameKey in _monitorTestPreviousRecognizedBuffs)
        {
            _recognizedBuffNameKeys.Add(nameKey);
        }
        foreach (var nameKey in _monitorTestPreviousSelectedBuffs)
        {
            _selectedBuffNameKeys.Add(nameKey);
        }
        _monitorTestPreviousRecognizedBuffs = [];
        _monitorTestPreviousSelectedBuffs = [];

        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, _buffMonitorEnabled, scheduleAutoSave: false);
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, _tuairimMonitorEnabled, scheduleAutoSave: false);
        SynchronizeAlertNotificationElement(scheduleAutoSave: false);
        _layoutCanvasHeight = _monitorTestPreviousLayoutCanvasHeight;
        UpdateBuffSelectionCheckStates();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        if (_overlayWindow is null)
        {
            _internalTimerDebugTimer.Stop();
        }
        else
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.MinValue;
            _ = SynchronizeMonitorValuesAsync("test-mode-ended");
        }
        UpdateMonitorControlAvailability();
        UpdateMonitorTestButtonPresentation();
        _log.Info("Monitor test mode stopped and temporary monitor values were cleared.");
        SetStatus("monitor.test.stopped");
    }

    private void UpdateMonitorTestButtonPresentation()
    {
        if (MonitorTestModeButton is null || MonitorTestMode2Button is null)
        {
            return;
        }
        MonitorTestModeButton.Content = L.T(
            _monitorTestMode && _monitorTestScenario == 1 ? "monitor.test.stop" : "monitor.test.start");
        MonitorTestMode2Button.Content = L.T(
            _monitorTestMode && _monitorTestScenario == 2 ? "monitor.test.stop" : "monitor.test.start.second");
    }

    private void AlertNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void BuffAlertSecondsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _buffAlertSeconds = ReadAlertThreshold(BuffAlertSecondsBox.Text, 5, 30, _buffAlertSeconds);
        BuffAlertSecondsBox.Text = _buffAlertSeconds.ToString();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertPercentBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _tuairimAlertPercent = ReadAlertThreshold(TuairimAlertPercentBox.Text, 90, 100, _tuairimAlertPercent);
        TuairimAlertPercentBox.Text = _tuairimAlertPercent.ToString();
        ScheduleProfileAutoSave();
    }

    private void BuffAlertVolumeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox || !TryReadSoundSlotTagIndex(textBox.Tag, out var index))
        {
            return;
        }
        SetBuffAlertVolume(index, ReadAlertVolume(textBox.Text, GetBuffAlertVolume(index)));
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertVolumeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _tuairimAlertVolume = ReadAlertVolume(TuairimAlertVolumeBox.Text, _tuairimAlertVolume);
        TuairimAlertVolumeBox.Text = _tuairimAlertVolume.ToString();
        ScheduleProfileAutoSave();
    }

    private void BuffAlertSoundModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMonitorAlertSettings ||
            BuffAlertSoundModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }
        _buffAlertSoundMode = mode == BuffSoundModeIndividual ? BuffSoundModeIndividual : BuffSoundModeGlobal;
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TuairimAlertFrequencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMonitorAlertSettings ||
            TuairimAlertFrequencyCombo.SelectedItem is not ComboBoxItem { Tag: string frequency })
        {
            return;
        }
        _tuairimAlertFrequency = frequency == TuairimAlertFrequencyEveryPercent
            ? TuairimAlertFrequencyEveryPercent
            : TuairimAlertFrequencyOnce;
        _tuairimAlertFired = false;
        ScheduleProfileAutoSave();
    }

    private void BrowseBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSoundSlotIndex(sender, out var index))
        {
            return;
        }
        var path = ChooseMonitorAlertSound();
        if (path is null)
        {
            return;
        }
        SetBuffAlertSoundPath(index, path);
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void BrowseTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseMonitorAlertSound();
        if (path is null)
        {
            return;
        }
        _tuairimAlertSoundPath = path;
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void ClearBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSoundSlotIndex(sender, out var index))
        {
            return;
        }
        SetBuffAlertSoundPath(index, string.Empty);
        var player = GetBuffAlertPlayer(index);
        player.Stop();
        player.Close();
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void ClearTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e)
    {
        _tuairimAlertSoundPath = string.Empty;
        _tuairimAlertPlayer.Stop();
        _tuairimAlertPlayer.Close();
        RefreshMonitorAlertSettingsControls();
        ScheduleProfileAutoSave();
    }

    private void TestBuffAlertSoundSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryReadSoundSlotIndex(sender, out var index))
        {
            PlayMonitorAlertSound(
                GetBuffAlertPlayer(index),
                GetBuffAlertSoundPath(index),
                GetBuffAlertVolume(index),
                $"buff-test:{index}");
        }
    }

    private void TestTuairimAlertSoundButton_Click(object sender, RoutedEventArgs e) =>
        PlayMonitorAlertSound(_tuairimAlertPlayer, _tuairimAlertSoundPath, _tuairimAlertVolume, "tuairim-test");

    private string? ChooseMonitorAlertSound()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("monitor.alert.sound.choose"),
            Filter = L.T("erin.audio.file.filter"),
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static bool TryReadSoundSlotIndex(object sender, out int index)
    {
        index = -1;
        return sender is Button { Tag: var tag } &&
               int.TryParse(tag?.ToString(), out index) &&
               index is >= 0 and < 4;
    }

    private static bool TryReadSoundSlotTagIndex(object? tag, out int index)
    {
        index = -1;
        return int.TryParse(tag?.ToString(), out index) && index is >= 0 and < 4;
    }

    private string GetBuffAlertSoundPath(int index)
    {
        if (_buffAlertSoundMode == BuffSoundModeGlobal)
        {
            return index == 0 ? _buffAlertSoundPath : string.Empty;
        }
        var nameKey = InternalBuffTimerPreviewRenderer.BuffNameKeys[index];
        return _buffAlertSoundPaths.GetValueOrDefault(nameKey, string.Empty);
    }

    private void SetBuffAlertSoundPath(int index, string path)
    {
        if (_buffAlertSoundMode == BuffSoundModeGlobal)
        {
            if (index == 0)
            {
                _buffAlertSoundPath = path;
            }
            return;
        }
        _buffAlertSoundPaths[InternalBuffTimerPreviewRenderer.BuffNameKeys[index]] = path;
    }

    private int GetBuffAlertVolume(int index)
    {
        if (_buffAlertSoundMode == BuffSoundModeGlobal)
        {
            return index == 0 ? _buffAlertVolume : 100;
        }
        return _buffAlertVolumes.GetValueOrDefault(InternalBuffTimerPreviewRenderer.BuffNameKeys[index], 100);
    }

    private void SetBuffAlertVolume(int index, int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (_buffAlertSoundMode == BuffSoundModeGlobal)
        {
            if (index == 0)
            {
                _buffAlertVolume = volume;
            }
            return;
        }
        _buffAlertVolumes[InternalBuffTimerPreviewRenderer.BuffNameKeys[index]] = volume;
    }

    private string ResolveBuffAlertSoundPath(string nameKey) =>
        _buffAlertSoundMode == BuffSoundModeIndividual
            ? _buffAlertSoundPaths.GetValueOrDefault(nameKey, string.Empty)
            : _buffAlertSoundPath;

    private int ResolveBuffAlertVolume(string nameKey) =>
        _buffAlertSoundMode == BuffSoundModeIndividual
            ? _buffAlertVolumes.GetValueOrDefault(nameKey, 100)
            : _buffAlertVolume;

    private MediaPlayer GetBuffAlertPlayer(int index) =>
        _buffAlertSoundMode == BuffSoundModeIndividual
            ? _individualBuffAlertPlayers[InternalBuffTimerPreviewRenderer.BuffNameKeys[index]]
            : _buffAlertPlayer;

    private MediaPlayer ResolveBuffAlertPlayer(string nameKey) =>
        _buffAlertSoundMode == BuffSoundModeIndividual
            ? _individualBuffAlertPlayers[nameKey]
            : _buffAlertPlayer;

    private static int ReadAlertThreshold(string? text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, out var value) && value >= minimum && value <= maximum ? value : fallback;

    private static int ReadAlertVolume(string? text, int fallback) =>
        int.TryParse(text, out var value) && value is >= 0 and <= 100 ? value : fallback;

    private void CommitMonitorAlertThresholdInputs()
    {
        if (BuffAlertSecondsBox is null)
        {
            return;
        }
        _buffAlertSeconds = ReadAlertThreshold(BuffAlertSecondsBox.Text, 5, 30, _buffAlertSeconds);
        _tuairimAlertPercent = ReadAlertThreshold(TuairimAlertPercentBox.Text, 90, 100, _tuairimAlertPercent);
        var volumeBoxes = new[] { BuffAlertVolumeBox1, BuffAlertVolumeBox2, BuffAlertVolumeBox3, BuffAlertVolumeBox4 };
        for (var index = 0; index < volumeBoxes.Length; index++)
        {
            SetBuffAlertVolume(index, ReadAlertVolume(volumeBoxes[index].Text, GetBuffAlertVolume(index)));
        }
        _tuairimAlertVolume = ReadAlertVolume(TuairimAlertVolumeBox.Text, _tuairimAlertVolume);
        BuffAlertSecondsBox.Text = _buffAlertSeconds.ToString();
        TuairimAlertPercentBox.Text = _tuairimAlertPercent.ToString();
    }

    private void RefreshMonitorAlertSettingsControls()
    {
        if (BuffAlertSecondsBox is null)
        {
            return;
        }
        _isUpdatingMonitorAlertSettings = true;
        try
        {
            BuffAlertSecondsBox.Text = _buffAlertSeconds.ToString();
            TuairimAlertPercentBox.Text = _tuairimAlertPercent.ToString();
            SelectComboBoxTag(BuffAlertSoundModeCombo, _buffAlertSoundMode);
            SelectComboBoxTag(TuairimAlertFrequencyCombo, _tuairimAlertFrequency);

            var individual = _buffAlertSoundMode == BuffSoundModeIndividual;
            BuffAlertSoundLabel1.Text = individual
                ? L.T("monitor.buff.sound.battle")
                : L.T("monitor.alert.sound.mode.global");
            BuffAlertSoundRow2.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow3.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;
            BuffAlertSoundRow4.Visibility = individual ? Visibility.Visible : Visibility.Collapsed;

            var pathBoxes = new[] { BuffAlertSoundPathBox1, BuffAlertSoundPathBox2, BuffAlertSoundPathBox3, BuffAlertSoundPathBox4 };
            var clearButtons = new[] { ClearBuffAlertSoundButton1, ClearBuffAlertSoundButton2, ClearBuffAlertSoundButton3, ClearBuffAlertSoundButton4 };
            var testButtons = new[] { TestBuffAlertSoundButton1, TestBuffAlertSoundButton2, TestBuffAlertSoundButton3, TestBuffAlertSoundButton4 };
            var volumeBoxes = new[] { BuffAlertVolumeBox1, BuffAlertVolumeBox2, BuffAlertVolumeBox3, BuffAlertVolumeBox4 };
            for (var index = 0; index < pathBoxes.Length; index++)
            {
                var path = GetBuffAlertSoundPath(index);
                pathBoxes[index].Text = path;
                volumeBoxes[index].Text = GetBuffAlertVolume(index).ToString();
                clearButtons[index].IsEnabled = !string.IsNullOrWhiteSpace(path);
                testButtons[index].IsEnabled = File.Exists(path);
            }

            TuairimAlertSoundPathBox.Text = _tuairimAlertSoundPath;
            TuairimAlertVolumeBox.Text = _tuairimAlertVolume.ToString();
            ClearTuairimAlertSoundButton.IsEnabled = !string.IsNullOrWhiteSpace(_tuairimAlertSoundPath);
            TestTuairimAlertSoundButton.IsEnabled = File.Exists(_tuairimAlertSoundPath);
        }
        finally
        {
            _isUpdatingMonitorAlertSettings = false;
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private void LoadStartupAlertSettings()
    {
        try
        {
            var profile = _profileStore.Load(ReadSelectedProfileName());
            if (profile is null)
            {
                return;
            }
            ApplyMonitorAlertSettings(profile);
            RefreshMonitorAlertSettingsControls();
        }
        catch (Exception exception)
        {
            _log.Error("Failed to load startup monitor alert settings.", exception);
        }
    }

    private void ApplyMonitorAlertSettings(OverlayProfile profile)
    {
        _buffAlertSeconds = profile.BuffAlertSeconds is >= 5 and <= 30 ? profile.BuffAlertSeconds : 30;
        _buffAlertSoundPath = profile.BuffAlertSoundPath ?? string.Empty;
        _buffAlertVolume = profile.BuffAlertVolume is >= 0 and <= 100 ? profile.BuffAlertVolume : 100;
        _buffAlertSoundMode = profile.BuffAlertSoundMode == BuffSoundModeIndividual
            ? BuffSoundModeIndividual
            : BuffSoundModeGlobal;
        _buffAlertSoundPaths.Clear();
        _buffAlertVolumes.Clear();
        foreach (var nameKey in InternalBuffTimerPreviewRenderer.BuffNameKeys)
        {
            if (profile.BuffAlertSoundPaths?.TryGetValue(nameKey, out var path) == true)
            {
                _buffAlertSoundPaths[nameKey] = path ?? string.Empty;
            }
            if (profile.BuffAlertVolumes?.TryGetValue(nameKey, out var volume) == true)
            {
                _buffAlertVolumes[nameKey] = Math.Clamp(volume, 0, 100);
            }
        }
        _tuairimAlertPercent = profile.TuairimAlertPercent is >= 90 and <= 100 ? profile.TuairimAlertPercent : 95;
        _tuairimAlertSoundPath = profile.TuairimAlertSoundPath ?? string.Empty;
        _tuairimAlertVolume = profile.TuairimAlertVolume is >= 0 and <= 100 ? profile.TuairimAlertVolume : 100;
        _tuairimAlertFrequency = profile.TuairimAlertFrequency == TuairimAlertFrequencyEveryPercent
            ? TuairimAlertFrequencyEveryPercent
            : TuairimAlertFrequencyOnce;
        _tuairimAlertFired = false;
        foreach (var timer in _internalBuffTimers)
        {
            timer.AlertFired = timer.RemainingSeconds <= _buffAlertSeconds;
        }
    }

    private void TryFireBuffAlert(InternalBuffTimer timer, int previousSeconds, int currentSeconds)
    {
        if (currentSeconds > _buffAlertSeconds)
        {
            timer.AlertFired = false;
            return;
        }
        if (timer.AlertFired || previousSeconds <= _buffAlertSeconds)
        {
            return;
        }

        timer.AlertFired = true;
        _log.Info(
            $"Buff alert threshold reached: key={timer.NameKey}, threshold={_buffAlertSeconds}, " +
            $"previous={previousSeconds}, current={currentSeconds}");
        _internalTimerOverlayWindow?.ShowBuffAlert(timer.NameKey, currentSeconds);
        PlayMonitorAlertSound(
            ResolveBuffAlertPlayer(timer.NameKey),
            ResolveBuffAlertSoundPath(timer.NameKey),
            ResolveBuffAlertVolume(timer.NameKey),
            $"buff:{timer.NameKey}");
    }

    private void TryFireTuairimAlert(int previousPercent, int currentPercent, bool hadAcceptedObservation)
    {
        if (!hadAcceptedObservation)
        {
            _tuairimAlertFired = currentPercent >= _tuairimAlertPercent;
            return;
        }
        if (currentPercent < _tuairimAlertPercent)
        {
            _tuairimAlertFired = false;
            return;
        }
        if (_tuairimAlertFrequency == TuairimAlertFrequencyEveryPercent)
        {
            if (currentPercent > previousPercent && currentPercent <= 99)
            {
                _log.Info(
                    $"Tuairim incremental alert: threshold={_tuairimAlertPercent}, " +
                    $"previous={previousPercent}, current={currentPercent}");
                _internalTimerOverlayWindow?.ShowTuairimAlert(currentPercent);
                PlayMonitorAlertSound(
                    _tuairimAlertPlayer,
                    _tuairimAlertSoundPath,
                    _tuairimAlertVolume,
                    $"tuairim:{currentPercent}");
            }
            return;
        }
        if (_tuairimAlertFired || previousPercent >= _tuairimAlertPercent)
        {
            return;
        }

        _tuairimAlertFired = true;
        _log.Info(
            $"Tuairim alert threshold reached: threshold={_tuairimAlertPercent}, " +
            $"previous={previousPercent}, current={currentPercent}");
        _internalTimerOverlayWindow?.ShowTuairimAlert(currentPercent);
        PlayMonitorAlertSound(_tuairimAlertPlayer, _tuairimAlertSoundPath, _tuairimAlertVolume, "tuairim");
    }

    private void PlayMonitorAlertSound(MediaPlayer player, string path, int volume, string alertKind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            player.Stop();
            player.Close();
            player.Open(new Uri(path, UriKind.Absolute));
            player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            player.Play();
            _log.Info($"Monitor alert sound played: kind={alertKind}, volume={volume}, path={path}");
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to play monitor alert sound: kind={alertKind}, path={path}", exception);
        }
    }

    private void CloseAlertPlayers()
    {
        _buffAlertPlayer.Stop();
        _buffAlertPlayer.Close();
        foreach (var player in _individualBuffAlertPlayers.Values)
        {
            player.Stop();
            player.Close();
        }
        _tuairimAlertPlayer.Stop();
        _tuairimAlertPlayer.Close();
    }

    private void DetectBuffWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_buffMonitorEnabled || _overlayWindow is not null)
        {
            return;
        }
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        SetMonitorDetectionMode(MonitorDetectionMode.BuffWindow);
        SetStatus("monitor.buff.detect.drag");
    }

    private void DetectTuairimUiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_tuairimMonitorEnabled || _overlayWindow is not null)
        {
            return;
        }
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        SetMonitorDetectionMode(MonitorDetectionMode.Tuairim);
        SetStatus("monitor.tuairim.detect.drag");
    }

    private void BuffSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string nameKey } checkBox ||
            _overlayWindow is not null ||
            !_recognizedBuffNameKeys.Contains(nameKey))
        {
            UpdateBuffSelectionCheckStates();
            return;
        }

        if (checkBox.IsChecked == true)
        {
            if (!CanAddBuffSelection(nameKey))
            {
                checkBox.IsChecked = false;
                SetStatus("monitor.buff.selection.invalid");
                return;
            }

            _selectedBuffNameKeys.Add(nameKey);
        }
        else
        {
            _selectedBuffNameKeys.Remove(nameKey);
        }

        RefreshInternalTimerElementPreviews();
        ScheduleProfileAutoSave();
    }

    private void ApplyRecognizedBuffs(IEnumerable<string> nameKeys)
    {
        _recognizedBuffNameKeys.Clear();
        foreach (var key in nameKeys.Where(InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains))
        {
            _recognizedBuffNameKeys.Add(key);
        }

        _selectedBuffNameKeys.RemoveWhere(key => !_recognizedBuffNameKeys.Contains(key));
        UpdateMonitorControlAvailability();
        RefreshInternalTimerElementPreviews();
        ScheduleProfileAutoSave();
    }

    private bool CanAddBuffSelection(string nameKey)
    {
        if (_selectedBuffNameKeys.Contains(nameKey))
        {
            return true;
        }
        if (_selectedBuffNameKeys.Count >= 2)
        {
            return false;
        }
        if (_selectedBuffNameKeys.Count == 0)
        {
            return true;
        }

        const string marchSongKey = "monitor.buff.march.song";
        return nameKey == marchSongKey || _selectedBuffNameKeys.Contains(marchSongKey);
    }

    private IEnumerable<CheckBox> BuffSelectionCheckBoxes()
    {
        yield return BattleOvertureBuffCheckBox;
        yield return MarchSongBuffCheckBox;
        yield return VivaceBuffCheckBox;
        yield return HarvestSongBuffCheckBox;
    }

    private void UpdateBuffSelectionCheckStates()
    {
        foreach (var checkBox in BuffSelectionCheckBoxes())
        {
            if (checkBox.Tag is not string nameKey)
            {
                continue;
            }

            checkBox.IsChecked = _selectedBuffNameKeys.Contains(nameKey);
        }
    }

    private void UpdateMonitorControlAvailability()
    {
        if (DetectBuffWindowButton is null)
        {
            return;
        }

        var baseEditable = _overlayWindow is null && !_isMonitorDetectionBusy;
        var settingsEditable = baseEditable &&
                               _monitorDetectionMode == MonitorDetectionMode.None &&
                               !_monitorTestMode;
        StartOverlayButton.IsEnabled = _overlayWindow is null;
        StopOverlayLayoutButton.IsEnabled = _overlayWindow is not null;
        BuffMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        TuairimMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        BuffAlertSettingsPanel.IsEnabled = settingsEditable;
        TuairimAlertSettingsPanel.IsEnabled = settingsEditable;
        DetectBuffWindowButton.IsEnabled = baseEditable &&
                                           !_monitorTestMode &&
                                           _buffMonitorEnabled &&
                                           _monitorDetectionMode is MonitorDetectionMode.None or MonitorDetectionMode.BuffWindow;
        DetectTuairimUiButton.IsEnabled = baseEditable &&
                                         !_monitorTestMode &&
                                         _tuairimMonitorEnabled &&
                                         _monitorDetectionMode is MonitorDetectionMode.None or MonitorDetectionMode.Tuairim;
        foreach (var checkBox in BuffSelectionCheckBoxes())
        {
            var nameKey = checkBox.Tag as string;
            checkBox.IsEnabled = settingsEditable &&
                                 _buffMonitorEnabled &&
                                 nameKey is not null &&
                                 _recognizedBuffNameKeys.Contains(nameKey);
        }
        UpdateBuffSelectionCheckStates();
    }

    private async void InternalTimerDebugTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var reachedVerificationPoint = false;
        foreach (var timer in _internalBuffTimers)
        {
            var previousSeconds = timer.RemainingSeconds;
            timer.RemainingSeconds = Math.Max(_monitorTestMode ? 0 : 1, timer.RemainingSeconds - 1);
            TryFireBuffAlert(timer, previousSeconds, timer.RemainingSeconds);
            reachedVerificationPoint |= previousSeconds > 30 && timer.RemainingSeconds <= 30;
        }

        _internalTimerOverlayWindow?.SetTimers(_internalBuffTimers);
        if (_monitorTestMode)
        {
            AdvanceMonitorTestTuairim();
            RefreshInternalTimerElementPreviews();
            return;
        }
        var needsFastVerification = _pendingInitialBuffMinuteValidation.Count > 0 ||
                                    _internalBuffTimers.Any(timer => timer.NeedsFastVerification);
        if (reachedVerificationPoint || needsFastVerification || now >= _nextMonitorValueRecognitionAt)
        {
            var reason = reachedVerificationPoint
                ? "threshold-30"
                : needsFastVerification ? "fast-verification" : "periodic";
            await SynchronizeMonitorValuesAsync(reason);
        }
    }

    private void AdvanceMonitorTestTuairim()
    {
        var previousPercent = _tuairimPercent;
        if (_tuairimPercent >= 100)
        {
            _monitorTestTuairimFullSeconds++;
            if (_monitorTestTuairimFullSeconds >= TuairimFullEffectSeconds)
            {
                _tuairimPercent = 0;
                _monitorTestTuairimFullSeconds = 0;
                _monitorTestTuairimChargeSeconds = 0;
                _tuairimAlertFired = false;
            }
        }
        else
        {
            _monitorTestTuairimChargeSeconds++;
            if (_monitorTestTuairimChargeSeconds >= TuairimNormalChargeSecondsPerPercent)
            {
                _monitorTestTuairimChargeSeconds = 0;
                _tuairimPercent++;
                TryFireTuairimAlert(previousPercent, _tuairimPercent, hadAcceptedObservation: true);
            }
        }

        if (_tuairimPercent != previousPercent)
        {
            _lastAcceptedTuairimPercentAt = DateTimeOffset.UtcNow;
            _internalTimerOverlayWindow?.SetTuairimPercent(_tuairimPercent);
        }
    }

    private void RefreshInternalTimerOverlay()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = null;
        if (!_buffMonitorEnabled && !_tuairimMonitorEnabled)
        {
            _internalTimerDebugTimer.Stop();
            return;
        }

        StartInternalTimerOverlay();
    }

    private void StartInternalTimerOverlay()
    {
        var timerSlot = _buffMonitorEnabled && _selectedBuffNameKeys.Count > 0
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.InternalBuffTimer)
            : null;
        var tuairimSlot = _tuairimMonitorEnabled
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.TuairimGauge)
            : null;
        var alertSlot = (_buffMonitorEnabled || _tuairimMonitorEnabled)
            ? _overlaySlots.FirstOrDefault(slot => slot.Kind == OverlayElementKind.AlertNotification)
            : null;
        if (timerSlot is null && tuairimSlot is null && alertSlot is null)
        {
            return;
        }

        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = new InternalTimerOverlayWindow(
            _layoutCanvasWidth,
            _layoutCanvasHeight,
            _overlayOpacity,
            timerSlot,
            tuairimSlot,
            alertSlot,
            _internalBuffTimers,
            _selectedBuffNameKeys)
        {
            Left = _overlayLeft,
            Top = _overlayTop
        };
        _internalTimerOverlayWindow.Show();
        _internalTimerOverlayWindow.UpdateLayout();
        _internalTimerOverlayWindow.SetTuairimPercent(_tuairimPercent);
        _nextMonitorValueRecognitionAt = DateTimeOffset.MinValue;
        _monitorValueRecognitionGeneration++;
        _internalTimerDebugTimer.Start();
        _ = SynchronizeMonitorValuesAsync("overlay-start");
    }

    private void RefreshInternalTimerElementPreviews()
    {
        SynchronizeMonitorElementDimensions();
        foreach (var slot in _overlaySlots.Where(slot => slot.Kind != OverlayElementKind.Quickslot))
        {
            slot.Preview = RenderMonitorElementPreview(slot.Kind);
        }
    }

    private async Task SynchronizeMonitorValuesAsync(string reason)
    {
        if (_monitorTestMode || _isMonitorValueRecognitionBusy || _overlayWindow is null)
        {
            return;
        }

        var shouldReadBuffs = _buffMonitorEnabled &&
                              _selectedBuffNameKeys.Count > 0 &&
                              _buffMonitorRoi is not null;
        var shouldReadTuairim = _tuairimMonitorEnabled && _tuairimAnchor is not null;
        if (!shouldReadBuffs && !shouldReadTuairim)
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }

        BitmapSource? frame;
        try
        {
            frame = CaptureMonitorFrame();
        }
        catch (Exception exception)
        {
            _log.Error("Monitor value capture failed.", exception);
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }

        if (frame is null)
        {
            _nextMonitorValueRecognitionAt = DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            return;
        }

        _isMonitorValueRecognitionBusy = true;
        var generation = _monitorValueRecognitionGeneration;
        try
        {
            if (shouldReadBuffs && _buffMonitorRoi is Rect buffRoi)
            {
                var previousMatches = _buffIconMatches.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var evaluatedMatches = await Task.Run(() =>
                    _monitorTemplateDetection.EvaluateBuffAnchors(frame, _buffIconMatches.Values.ToArray()));
                if (generation != _monitorValueRecognitionGeneration || _overlayWindow is null)
                {
                    return;
                }
                foreach (var match in evaluatedMatches)
                {
                    _buffIconMatches[match.NameKey] = match;
                }

                foreach (var nameKey in _selectedBuffNameKeys.ToArray())
                {
                    var match = evaluatedMatches.FirstOrDefault(candidate => candidate.NameKey == nameKey);
                    if (match is null)
                    {
                        ResetPendingTimeObservation(nameKey);
                        ResetBuffZeroConfirmation(nameKey);
                        continue;
                    }

                    if (!match.IsActive && match.StateConfidence >= 0.03)
                    {
                        _pendingInitialBuffMinuteValidation.Remove(nameKey);
                        RegisterBuffZeroConfirmation(nameKey, reason, "inactive");
                        continue;
                    }
                    if (!match.IsActive)
                    {
                        _pendingInitialBuffMinuteValidation.Remove(nameKey);
                        ResetBuffZeroConfirmation(nameKey);
                        continue;
                    }

                    var transitionedFromOff = previousMatches.TryGetValue(nameKey, out var previousMatch) &&
                                              !previousMatch.IsActive &&
                                              _internalBuffTimers.All(timer => timer.NameKey != nameKey);
                    if (transitionedFromOff)
                    {
                        _pendingInitialBuffMinuteValidation.Add(nameKey);
                    }

                    var read = await _monitorValueRecognition.ReadBuffTimeAsync(frame, buffRoi, match.Bounds);
                    if (generation != _monitorValueRecognitionGeneration || _overlayWindow is null)
                    {
                        return;
                    }
                    if (read.RemainingSeconds is int remainingSeconds)
                    {
                        ApplyBuffTimeObservation(nameKey, remainingSeconds, read.RecognizedText, reason);
                    }
                    else
                    {
                        ResetPendingTimeObservation(nameKey);
                        ResetBuffZeroConfirmation(nameKey);
                        SaveMonitorDiagnosticOnce(frame, read.Bounds, $"buff-{SanitizeDiagnosticName(nameKey)}");
                    }

                    _log.Info(
                        $"Buff OCR: reason={reason}, key={nameKey}, active={match.IsActive}, " +
                        $"stateConfidence={match.StateConfidence:0.000}, seconds={read.RemainingSeconds?.ToString() ?? "none"}, " +
                        $"bounds={FormatRect(read.Bounds)}, text={read.RecognizedText}");
                }
            }

            if (shouldReadTuairim && _tuairimAnchor is Rect tuairimAnchor)
            {
                var read = await _monitorValueRecognition.ReadTuairimPercentAsync(frame, tuairimAnchor);
                if (generation != _monitorValueRecognitionGeneration || _overlayWindow is null)
                {
                    return;
                }
                if (read.Percent is int percent)
                {
                    ApplyTuairimPercentObservation(percent, reason);
                }
                else
                {
                    SaveMonitorDiagnosticOnce(frame, read.Bounds, "tuairim-percent");
                }

                _log.Info(
                    $"Tuairim OCR: reason={reason}, percent={read.Percent?.ToString() ?? "none"}, " +
                    $"bounds={FormatRect(read.Bounds)}, text={read.RecognizedText}");
            }

            _internalTimerOverlayWindow?.SetTimers(_internalBuffTimers);
            RefreshInternalTimerElementPreviews();
        }
        catch (Exception exception)
        {
            _log.Error("Monitor value recognition failed.", exception);
        }
        finally
        {
            _isMonitorValueRecognitionBusy = false;
            if (generation == _monitorValueRecognitionGeneration)
            {
                var needsFastRetry = _pendingInitialBuffMinuteValidation.Count > 0 ||
                                     _internalBuffTimers.Any(timer => timer.NeedsFastVerification);
                _nextMonitorValueRecognitionAt = needsFastRetry
                    ? DateTimeOffset.UtcNow
                    : DateTimeOffset.UtcNow.AddSeconds(MonitorRecognitionIntervalSeconds - 1);
            }
        }
    }

    private void ApplyTuairimPercentObservation(int observedPercent, string reason)
    {
        observedPercent = Math.Clamp(observedPercent, 0, 100);
        var hadAcceptedObservation = _hasTuairimPercentObservation;
        var previousPercent = _tuairimPercent;
        if (!_hasTuairimPercentObservation)
        {
            if (!ConfirmInitialTuairimPercent(observedPercent, reason))
            {
                return;
            }
        }
        else if (observedPercent == 0 && _tuairimPercent != 0)
        {
            if (_pendingTuairimPercent == 0)
            {
                _pendingTuairimPercentConfirmations++;
            }
            else
            {
                _pendingTuairimPercent = 0;
                _pendingTuairimPercentConfirmations = 1;
            }

            _log.Info(
                $"Tuairim zero validating: reason={reason}, count={_pendingTuairimPercentConfirmations}/2");
            if (_pendingTuairimPercentConfirmations < 2)
            {
                return;
            }
        }
        else
        {
            var elapsedSeconds = Math.Max(
                MonitorRecognitionIntervalSeconds,
                (DateTimeOffset.UtcNow - (_lastAcceptedTuairimPercentAt ?? DateTimeOffset.UtcNow)).TotalSeconds);
            var elapsedIntervals = Math.Max(
                1,
                (int)Math.Floor((elapsedSeconds + 0.25) / MonitorRecognitionIntervalSeconds));
            var allowedIncrease = Math.Max(
                5,
                elapsedIntervals * 5);
            if (observedPercent < _tuairimPercent || observedPercent > _tuairimPercent + allowedIncrease)
            {
                _log.Info(
                    $"Tuairim OCR rejected implausible change: reason={reason}, current={_tuairimPercent}, " +
                    $"observed={observedPercent}, allowedIncrease={allowedIncrease}");
                return;
            }
        }

        ClearPendingTuairimPercent();
        _hasTuairimPercentObservation = true;
        _tuairimPercent = observedPercent;
        _lastAcceptedTuairimPercentAt = DateTimeOffset.UtcNow;
        _internalTimerOverlayWindow?.SetTuairimPercent(observedPercent);
        TryFireTuairimAlert(previousPercent, observedPercent, hadAcceptedObservation);
    }

    private bool ConfirmInitialTuairimPercent(int observedPercent, string reason)
    {
        if (_pendingTuairimPercent is int pending &&
            observedPercent >= pending &&
            observedPercent <= pending + 5)
        {
            _pendingTuairimPercentConfirmations++;
        }
        else
        {
            _pendingTuairimPercent = observedPercent;
            _pendingTuairimPercentConfirmations = 1;
        }

        _log.Info(
            $"Tuairim initial validating: reason={reason}, observed={observedPercent}, " +
            $"count={_pendingTuairimPercentConfirmations}/2");
        return _pendingTuairimPercentConfirmations >= 2;
    }

    private void ClearPendingTuairimPercent()
    {
        _pendingTuairimPercent = null;
        _pendingTuairimPercentConfirmations = 0;
    }

    private void ResetTuairimPercentRecognitionState()
    {
        _hasTuairimPercentObservation = false;
        _lastAcceptedTuairimPercentAt = null;
        _tuairimAlertFired = false;
        ClearPendingTuairimPercent();
    }

    private void ApplyBuffTimeObservation(string nameKey, int observedSeconds, string recognizedText, string reason)
    {
        var timer = _internalBuffTimers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        var previousSeconds = timer?.RemainingSeconds;
        var recognizedTuan =
            recognizedText.Contains("\uD22C\uC548", StringComparison.Ordinal) ||
            recognizedText.Contains("\uC758 \uB178\uB798", StringComparison.Ordinal) ||
            recognizedText.Contains("\uC758\uB178\uB798", StringComparison.Ordinal);
        if (observedSeconds <= 0)
        {
            RegisterBuffZeroConfirmation(nameKey, reason, "ocr-zero");
            return;
        }

        if (timer is null &&
            _pendingInitialBuffMinuteValidation.Contains(nameKey) &&
            observedSeconds < 60)
        {
            _log.Info(
                $"Buff OCR rejected short initial activation: reason={reason}, key={nameKey}, observed={observedSeconds}");
            return;
        }

        _pendingInitialBuffMinuteValidation.Remove(nameKey);

        if (timer is null)
        {
            timer = new InternalBuffTimer(nameKey, observedSeconds);
            timer.AlertFired = observedSeconds <= _buffAlertSeconds;
            _internalBuffTimers.Add(timer);
        }
        else
        {
            timer.ConsecutiveZeroConfirmations = 0;
            if (recognizedTuan && !timer.HasTuanExtension && timer.RemainingSeconds <= 5)
            {
                timer.AwaitingTuanExtensionRefresh = true;
            }
            timer.HasTuanExtension |= recognizedTuan;

            if (timer.AwaitingTuanExtensionRefresh &&
                timer.RemainingSeconds <= 5 &&
                observedSeconds > 30)
            {
                timer.RemainingSeconds = observedSeconds;
                timer.AwaitingTuanExtensionRefresh = false;
                ClearPendingTimeObservation(timer);
                _log.Info(
                    $"Buff OCR applied Tuan extension immediately: reason={reason}, key={nameKey}, observed={observedSeconds}");
            }
            else
            {
                var difference = observedSeconds - timer.RemainingSeconds;
                if (difference < -3)
                {
                    var now = DateTimeOffset.UtcNow;
                    var continuesDownwardObservation = timer.PendingObservationIsDownward &&
                                                       timer.PendingObservedSeconds is int pending &&
                                                       observedSeconds <= pending + 1 &&
                                                       observedSeconds >= pending - 4;
                    if (continuesDownwardObservation)
                    {
                        timer.PendingObservedSeconds = observedSeconds;
                        timer.PendingObservationConfirmations++;
                    }
                    else
                    {
                        timer.PendingObservedSeconds = observedSeconds;
                        timer.PendingObservationConfirmations = 1;
                        timer.PendingObservationStartedAt = now;
                        timer.PendingObservationIsDownward = true;
                    }

                    var validFor = now - (timer.PendingObservationStartedAt ?? now);
                    if (validFor >= TimeSpan.FromSeconds(5) && timer.PendingObservationConfirmations >= 5)
                    {
                        timer.RemainingSeconds = observedSeconds;
                        ClearPendingTimeObservation(timer);
                        _log.Info(
                            $"Buff OCR accepted sustained downward value: reason={reason}, key={nameKey}, " +
                            $"observed={observedSeconds}, validMs={validFor.TotalMilliseconds:0}");
                    }
                    else
                    {
                        _log.Info(
                            $"Buff OCR validating downward value: reason={reason}, key={nameKey}, " +
                            $"current={timer.RemainingSeconds}, observed={observedSeconds}, " +
                            $"count={timer.PendingObservationConfirmations}, validMs={validFor.TotalMilliseconds:0}");
                    }
                }
                else if (difference <= 12)
                {
                    timer.RemainingSeconds = observedSeconds;
                    ClearPendingTimeObservation(timer);
                }
                else if (!timer.PendingObservationIsDownward &&
                         timer.PendingObservedSeconds is int pending &&
                         Math.Abs(pending - observedSeconds) <= 4)
                {
                    timer.PendingObservationConfirmations++;
                    if (timer.PendingObservationConfirmations >= 2)
                    {
                        timer.RemainingSeconds = observedSeconds;
                        ClearPendingTimeObservation(timer);
                    }
                }
                else
                {
                    timer.PendingObservedSeconds = observedSeconds;
                    timer.PendingObservationConfirmations = 1;
                    timer.PendingObservationStartedAt = DateTimeOffset.UtcNow;
                    timer.PendingObservationIsDownward = false;
                    _log.Info(
                        $"Buff OCR deferred: reason={reason}, key={nameKey}, current={timer.RemainingSeconds}, observed={observedSeconds}");
                }
            }
        }

        timer.LastRecognizedText = recognizedText;
        timer.HasTuanExtension |= recognizedTuan;
        timer.HasHarmony = recognizedText.Contains("\uD558\uBAA8\uB2C8", StringComparison.Ordinal);
        if (previousSeconds is int previous)
        {
            TryFireBuffAlert(timer, previous, timer.RemainingSeconds);
        }
    }

    private void RegisterBuffZeroConfirmation(string nameKey, string reason, string source)
    {
        var timer = _internalBuffTimers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is null)
        {
            return;
        }

        timer.RemainingSeconds = Math.Max(1, timer.RemainingSeconds);
        ClearPendingTimeObservation(timer);
        timer.ConsecutiveZeroConfirmations++;
        _log.Info(
            $"Buff zero confirmation: reason={reason}, key={nameKey}, source={source}, " +
            $"count={timer.ConsecutiveZeroConfirmations}/5");
        if (timer.ConsecutiveZeroConfirmations >= 5)
        {
            _internalBuffTimers.Remove(timer);
            _pendingInitialBuffMinuteValidation.Remove(nameKey);
            _log.Info($"Buff expired after confirmation: key={nameKey}");
        }
    }

    private void ResetPendingTimeObservation(string nameKey)
    {
        var timer = _internalBuffTimers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is not null)
        {
            ClearPendingTimeObservation(timer);
        }
    }

    private void ResetBuffZeroConfirmation(string nameKey)
    {
        var timer = _internalBuffTimers.FirstOrDefault(candidate => candidate.NameKey == nameKey);
        if (timer is not null)
        {
            timer.ConsecutiveZeroConfirmations = 0;
        }
    }

    private static void ClearPendingTimeObservation(InternalBuffTimer timer)
    {
        timer.PendingObservedSeconds = null;
        timer.PendingObservationConfirmations = 0;
        timer.PendingObservationStartedAt = null;
        timer.PendingObservationIsDownward = false;
    }

    private BitmapSource? CaptureMonitorFrame()
        => _captureSession.CaptureCurrentFrame(CurrentCaptureBackend);

    private void SaveMonitorDiagnosticOnce(BitmapSource source, Rect bounds, string kind)
    {
        if (!_monitorDiagnosticKindsSaved.Add(kind))
        {
            return;
        }

        try
        {
            var prefix = $"monitor-{_log.SessionStartedAt:yyyyMMdd-HHmmss}-{kind}";
            MonitorValueRecognitionService.SaveDiagnosticImages(source, bounds, _log.LogDirectory, prefix);
            _log.Info($"Monitor OCR diagnostic saved: prefix={prefix}, bounds={FormatRect(bounds)}");
        }
        catch (Exception exception)
        {
            _log.Error($"Monitor OCR diagnostic save failed: kind={kind}", exception);
        }
    }

    private static string SanitizeDiagnosticName(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingProfileSelection || ProfileCombo.SelectedItem is not string)
        {
            return;
        }

        FlushProfileAutoSave();
    }

    private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        FlushProfileAutoSave();
        var dialog = new ProfileNameDialog(string.Empty) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            SetStatus("Profile creation canceled.");
            return;
        }

        var profileName = dialog.ProfileName;
        if (_profileStore.Exists(profileName) && MessageBox.Show(
                this,
                L.F("profile.exists.confirm", profileName),
                L.T("confirm.replace"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("Profile creation canceled.");
            return;
        }

        _selectedProfileName = profileName;
        SaveActiveProfile(showStatus: true);
        RefreshProfileList(profileName);
    }

    private OverlayProfile BuildCurrentProfile(string profileName)
    {
        SaveCurrentSectionSettings();
        CommitMonitorAlertThresholdInputs();
        var profile = OverlayProfileMapper.CreateWorkspaceProfile(
            profileName,
            _workspace,
            ReadSlotInnerWidth(),
            ReadSlotInnerHeight(),
            RefreshIntervalFromFps(_refreshFps));

        profile.BuffMonitorEnabled = _buffMonitorEnabled;
        profile.TuairimMonitorEnabled = _tuairimMonitorEnabled;
        profile.BuffAlertSeconds = _buffAlertSeconds;
        profile.BuffAlertSoundPath = _buffAlertSoundPath;
        profile.BuffAlertVolume = _buffAlertVolume;
        profile.BuffAlertSoundMode = _buffAlertSoundMode;
        profile.BuffAlertSoundPaths = new Dictionary<string, string>(_buffAlertSoundPaths, StringComparer.Ordinal);
        profile.BuffAlertVolumes = new Dictionary<string, int>(_buffAlertVolumes, StringComparer.Ordinal);
        profile.TuairimAlertPercent = _tuairimAlertPercent;
        profile.TuairimAlertSoundPath = _tuairimAlertSoundPath;
        profile.TuairimAlertVolume = _tuairimAlertVolume;
        profile.TuairimAlertFrequency = _tuairimAlertFrequency;
        profile.RecognizedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
            .Where(_recognizedBuffNameKeys.Contains)
            .ToList();
        profile.SelectedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
            .Where(_selectedBuffNameKeys.Contains)
            .ToList();
        profile.BuffMonitorRoi = ToProfileRect(_buffMonitorRoi);
        profile.BuffAnchors = _buffIconMatches.Values
            .OrderBy(match => match.Bounds.Y)
            .Select(match => new OverlayProfileBuffAnchor
            {
                NameKey = match.NameKey,
                Bounds = ToProfileRect(match.Bounds)!,
                StructureScore = match.StructureScore,
                IsActive = match.IsActive,
                StateConfidence = match.StateConfidence
            })
            .ToList();
        profile.TuairimMonitorRoi = ToProfileRect(_tuairimMonitorRoi);
        profile.TuairimAnchor = ToProfileRect(_tuairimAnchor);
        return profile;
    }

    private void ScheduleProfileAutoSave()
    {
        if (_isLoadingProfile || _monitorTestMode || !IsLoaded)
        {
            return;
        }

        _isProfileDirty = true;
        _profileAutoSaveTimer.Stop();
        _profileAutoSaveTimer.Start();
    }

    private void FlushProfileAutoSave()
    {
        _profileAutoSaveTimer.Stop();
        if (!_isLoadingProfile && IsLoaded && _isProfileDirty)
        {
            _isProfileDirty = false;
            SaveActiveProfile(showStatus: false);
        }
    }

    private void SaveActiveProfile(bool showStatus)
    {
        try
        {
            var profileName = ReadSelectedProfileName();
            var profile = BuildCurrentProfile(profileName);
            var path = _profileStore.Save(profile, profileName);
            _selectedProfileName = System.IO.Path.GetFileNameWithoutExtension(path);
            _isProfileDirty = false;
            if (showStatus)
            {
                _log.Info($"Profile created: {path}, candidates={profile.Candidates.Count}, slots={profile.Slots.Count}");
                SetStatus(L.F("Profile created: {0}", path));
            }
        }
        catch (Exception exception)
        {
            _isProfileDirty = true;
            _log.Error("Profile auto-save failed.", exception);
            SetStatus(L.F("Profile save failed: {0}", exception.Message));
        }
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e) => LoadSelectedProfile();

    private void LoadSelectedProfile()
    {
        FlushProfileAutoSave();
        var profileName = ReadProfileComboName();
        OverlayProfile? profile;
        try
        {
            profile = _profileStore.Load(profileName);
        }
        catch (Exception exception)
        {
            var path = _profileStore.GetProfilePath(profileName);
            _log.Error($"Profile load failed: {path}.", exception);
            SetStatus(L.F("profile.load.failed.arg", path, exception.Message));
            return;
        }

        if (profile is null)
        {
            SetStatus(L.F("No saved profile exists: {0}", _profileStore.GetProfilePath(profileName)));
            return;
        }

        var savedKinds = profile.Candidates.ToDictionary(candidate => candidate.Id, candidate => candidate.Kind);
        if (_capturedImage is null && profile.Slots.Any(slot =>
                !savedKinds.TryGetValue(slot.SourceCandidateId, out var kind) || kind == OverlayElementKind.Quickslot))
        {
            SetStatus("Capture the game window before loading a profile with quickslots.");
            return;
        }

        _isLoadingProfile = true;
        try
        {
            RefreshProfileList(profileName);
            var refreshFps = CoerceRefreshFps(profile.RefreshFps > 0
                ? profile.RefreshFps
                : FpsFromInterval(profile.RefreshIntervalMs));
            OverlayProfileMapper.ApplyLayoutAndSectionSettings(profile, _workspace, refreshFps);
        _buffMonitorEnabled = profile.BuffMonitorEnabled;
        _tuairimMonitorEnabled = profile.TuairimMonitorEnabled;
        ApplyMonitorAlertSettings(profile);
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _pendingInitialBuffMinuteValidation.Clear();
        _buffIconMatches.Clear();
        _buffMonitorRoi = FromProfileRect(profile.BuffMonitorRoi);
        _tuairimMonitorRoi = FromProfileRect(profile.TuairimMonitorRoi);
        _tuairimAnchor = FromProfileRect(profile.TuairimAnchor);
        ResetTuairimPercentRecognitionState();
        if (_buffMonitorEnabled)
        {
            foreach (var key in (profile.RecognizedBuffNameKeys ?? []).Where(InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains))
            {
                _recognizedBuffNameKeys.Add(key);
            }
            foreach (var key in InternalBuffTimerPreviewRenderer.BuffNameKeys.Where((profile.SelectedBuffNameKeys ?? []).Contains))
            {
                if (_recognizedBuffNameKeys.Contains(key) && CanAddBuffSelection(key))
                {
                    _selectedBuffNameKeys.Add(key);
                }
            }
            foreach (var savedAnchor in profile.BuffAnchors ?? [])
            {
                var bounds = FromProfileRect(savedAnchor.Bounds);
                if (bounds is null || !InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains(savedAnchor.NameKey))
                {
                    continue;
                }

                _buffIconMatches[savedAnchor.NameKey] = new BuffIconMatch(
                    savedAnchor.NameKey,
                    bounds.Value,
                    savedAnchor.StructureScore,
                    savedAnchor.IsActive,
                    savedAnchor.StateConfidence);
            }
        }
        BuffMonitorEnabledCheckBox.IsChecked = _buffMonitorEnabled;
        TuairimMonitorEnabledCheckBox.IsChecked = _tuairimMonitorEnabled;
        RefreshMonitorAlertSettingsControls();
        var profileWidth = profile.SlotInnerWidth > 0 ? profile.SlotInnerWidth : profile.SlotInnerSize;
        var profileHeight = profile.SlotInnerHeight > 0 ? profile.SlotInnerHeight : profile.SlotInnerSize;
        SlotWidthBox.Text = ReadSlotDimensionText(profileWidth, ReadSlotInnerWidth());
        SlotHeightBox.Text = ReadSlotDimensionText(profileHeight, ReadSlotInnerHeight());
        ApplyProfileSectionSettingsToControls();

        _overlaySlots.Clear();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();

        var loadedCandidates = new Dictionary<int, SlotCandidate>();
        if (profile.Candidates.Count > 0)
        {
            foreach (var savedCandidate in profile.Candidates.OrderBy(candidate => candidate.Id))
            {
                if (savedCandidate.Kind != OverlayElementKind.Quickslot &&
                    !IsMonitorElementEnabled(savedCandidate.Kind))
                {
                    continue;
                }

                var candidate = new SlotCandidate(
                    savedCandidate.Id,
                    new Rect(
                        savedCandidate.SourceX,
                        savedCandidate.SourceY,
                        savedCandidate.SourceWidth,
                        savedCandidate.SourceHeight),
                    savedCandidate.Score,
                    savedCandidate.Kind,
                    savedCandidate.DisplayNameKey,
                    savedCandidate.IsBuiltIn)
                {
                    IsSelected = savedCandidate.IsSelected
                };
                AddCandidate(candidate);
                loadedCandidates[candidate.Id] = candidate;
            }
        }

        if (_buffMonitorEnabled)
        {
            var internalTimerCandidate = EnsureInternalTimerCandidate();
            loadedCandidates[internalTimerCandidate.Id] = internalTimerCandidate;
        }
        if (_tuairimMonitorEnabled)
        {
            var tuairimGaugeCandidate = EnsureTuairimGaugeCandidate();
            loadedCandidates[tuairimGaugeCandidate.Id] = tuairimGaugeCandidate;
        }
        if (_buffMonitorEnabled || _tuairimMonitorEnabled)
        {
            var alertCandidate = EnsureAlertNotificationCandidate();
            loadedCandidates[alertCandidate.Id] = alertCandidate;
        }

        foreach (var savedSection in profile.Sections)
        {
            if (!loadedCandidates.TryGetValue(savedSection.SeedCandidateId, out var seed))
            {
                continue;
            }

            var sectionCandidates = savedSection.CandidateIds
                .Select(id => loadedCandidates.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate is not null)
                .Cast<SlotCandidate>()
                .ToList();
            if (sectionCandidates.Count == 0)
            {
                continue;
            }

            _sections.Add(new QuickslotSection(
                savedSection.Id,
                seed,
                savedSection.PatternIndex,
                new SectionSettings(savedSection.SmallGapX, savedSection.SmallGapY, savedSection.LargeGap),
                sectionCandidates));
        }

        var nextCandidateId = loadedCandidates.Keys.Where(id => id > 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var savedSlot in profile.Slots)
        {
            if (savedKinds.TryGetValue(savedSlot.SourceCandidateId, out var savedKind) &&
                savedKind != OverlayElementKind.Quickslot &&
                !IsMonitorElementEnabled(savedKind))
            {
                continue;
            }

            var candidate = ResolveProfileSlotSource(savedSlot, loadedCandidates);
            if (candidate is null)
            {
                candidate = new SlotCandidate(
                    nextCandidateId++,
                    new Rect(savedSlot.SourceX, savedSlot.SourceY, savedSlot.SourceWidth, savedSlot.SourceHeight),
                    100);
                AddCandidate(candidate);
                loadedCandidates[candidate.Id] = candidate;
            }

            var crop = candidate.Kind == OverlayElementKind.Quickslot
                ? _captureSession.Crop(_capturedImage!, candidate.SourceRect)
                : RenderMonitorElementPreview(candidate.Kind);
            var hasOpacityOverride = savedSlot.HasOpacityOverride || Math.Abs(savedSlot.Opacity - 1) > 0.001;
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop,
                savedSlot.Opacity > 0 ? savedSlot.Opacity : 1,
                savedSlot.Scale > 0 ? savedSlot.Scale : InferSlotScale(savedSlot),
                hasOpacityOverride);
            _overlaySlots.Add(slot);
        }

            EnsureEnabledMonitorElementsPlaced();

            _nextSectionId = _sections.Count == 0 ? 1 : _sections.Max(section => section.Id) + 1;
            RefreshSectionLabels();
            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
            RefreshInternalTimerElementPreviews();
            UpdateMonitorControlAvailability();
            RefreshMonitorDetectionVisuals();
            var path = _profileStore.GetProfilePath(profileName);
            _log.Info(
                $"Profile loaded: {path}, candidates={_candidates.Count}, slots={profile.Slots.Count}, " +
                $"recoveredFromBackup={_profileStore.LastLoadRecoveredFromBackup}");
            SetStatus(_profileStore.LastLoadRecoveredFromBackup
                ? L.F("profile.loaded.from.backup.arg", path, _candidates.Count, profile.Slots.Count)
                : L.F("Profile loaded: {0} ({1} candidates, {2} slots).", path, _candidates.Count, profile.Slots.Count));
        }
        catch (Exception exception)
        {
            var path = _profileStore.GetProfilePath(profileName);
            _log.Error($"Validated profile could not be applied: {path}.", exception);
            SetStatus(L.F("profile.apply.failed.arg", path, exception.Message));
        }
        finally
        {
            _profileAutoSaveTimer.Stop();
            _isProfileDirty = false;
            _isLoadingProfile = false;
        }
    }

    private async void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow is not null)
        {
            return;
        }

        var hasSlotOverlay = _overlaySlots.Any(slot => slot.Kind == OverlayElementKind.Quickslot);
        var hasBuffOverlay = _buffMonitorEnabled &&
                             _selectedBuffNameKeys.Count > 0 &&
                             _overlaySlots.Any(slot => slot.Kind == OverlayElementKind.InternalBuffTimer);
        var hasTuairimOverlay = _tuairimMonitorEnabled &&
                                _overlaySlots.Any(slot => slot.Kind == OverlayElementKind.TuairimGauge);
        if (!hasSlotOverlay && !hasBuffOverlay && !hasTuairimOverlay)
        {
            SetStatus("No slots are placed on the overlay canvas.");
            return;
        }

        var captureBackend = CurrentCaptureBackend;
        var hasMonitorOverlay = hasBuffOverlay || hasTuairimOverlay;
        var requiresLiveCapture = hasSlotOverlay || (hasMonitorOverlay && !_monitorTestMode);
        if (requiresLiveCapture &&
            captureBackend != CaptureBackend.Wgc &&
            _selectedWindow is null)
        {
            SetStatus(L.F("Run Auto capture or Manual capture before starting the overlay with {0}.", L.T(CaptureBackendLabel(captureBackend))));
            return;
        }
        if (requiresLiveCapture &&
            captureBackend == CaptureBackend.Wgc &&
            _wgcSelection is null)
        {
            SetStatus(L.F("Run Auto capture or Manual capture before starting the overlay with {0}.", L.T(CaptureBackendLabel(captureBackend))));
            return;
        }

        if (requiresLiveCapture && captureBackend == CaptureBackend.Wgc)
        {
            await _captureSession.EnsureBorderlessAccessAsync();
        }

        try
        {
            StopOverlay(setStatus: false);
            if (!RegisterStopHotkey())
            {
                return;
            }

            _overlayWindow = new OverlayWindow(_layoutCanvasWidth, _layoutCanvasHeight, _overlayOpacity, _overlaySlots)
            {
                Left = _overlayLeft,
                Top = _overlayTop
            };
            _overlayWindow.Show();
            _overlayWindow.UpdateLayout();

            if (_overlayWindow.ClickThroughConfigurationException is not null ||
                !_overlayWindow.IsClickThroughConfigured ||
                !_overlayWindow.IsNoActivateConfigured ||
                !_overlayWindow.IsTopmostConfigured ||
                !_overlayWindow.IsInputHookConfigured)
            {
                var detail = _overlayWindow.ClickThroughConfigurationException?.Message ??
                             $"exStyle=0x{_overlayWindow.AppliedExtendedStyle.ToInt64():X16}, " +
                             $"clickThrough={_overlayWindow.IsClickThroughConfigured}, " +
                             $"noActivate={_overlayWindow.IsNoActivateConfigured}, " +
                             $"topmost={_overlayWindow.IsTopmostConfigured}, " +
                             $"inputHook={_overlayWindow.IsInputHookConfigured}";

                _log.Error(
                    "Overlay click-through configuration failed.",
                    _overlayWindow.ClickThroughConfigurationException ?? new InvalidOperationException(detail));

                _overlayWindow.Close();
                _overlayWindow = null;
                _captureSession.StopLiveWgcCapture();
                _liveOverlayTimer.Stop();

                SetStatus(L.F("Overlay click-through configuration failed: {0}", detail));
                return;
            }

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(_refreshFps));
            _activeRenderMode = _appSettings.OverlayRenderMode;

            ResetCpuRenderStats();
            var rendererMode = RenderModeLabel(_activeRenderMode);
            if (!hasSlotOverlay)
            {
                rendererMode = "monitor.internal.overlay";
            }
            else if (hasSlotOverlay &&
                     _activeRenderMode == OverlayRenderMode.GpuDxgi &&
                     captureBackend == CaptureBackend.Wgc &&
                     _wgcSelection is not null)
            {
                try
                {
                    _gpuLiveOverlayService = new GpuLiveOverlayService(
                        new WindowInteropHelper(_overlayWindow).Handle,
                        (int)Math.Ceiling(_layoutCanvasWidth),
                        (int)Math.Ceiling(_layoutCanvasHeight),
                        _wgcSelection.Item,
                        _overlaySlots,
                        _overlayOpacity,
                        _refreshFps,
                        _captureSession.IsBorderlessCaptureAllowed,
                        _log);
                    _overlayWindow.RenderSlots(Array.Empty<OverlaySlot>());
                    _gpuLiveOverlayService.Start();
                    rendererMode = RenderModeLabel(OverlayRenderMode.GpuDxgi);
                }
                catch (Exception gpuEx)
                {
                    _gpuLiveOverlayService?.Dispose();
                    _gpuLiveOverlayService = null;
                    _log.Error("GPU live overlay renderer initialization failed. Falling back to CPU renderer.", gpuEx);
                    _activeRenderMode = OverlayRenderMode.CpuWpf;
                    rendererMode = $"{RenderModeLabel(OverlayRenderMode.CpuWpf)} fallback";
                    _captureSession.StartLiveWgcCapture();
                }
            }
            else if (_activeRenderMode == OverlayRenderMode.GpuDxgi)
            {
                _activeRenderMode = OverlayRenderMode.CpuWpf;
                rendererMode = $"{RenderModeLabel(OverlayRenderMode.CpuWpf)} fallback";
                _log.Info($"GPU/DXGI renderer requested with captureBackend={captureBackend}. Falling back to CPU/WPF renderer.");
            }
            else if (requiresLiveCapture && captureBackend == CaptureBackend.Wgc && _wgcSelection is not null)
            {
                _captureSession.StartLiveWgcCapture();
            }

            if (hasMonitorOverlay &&
                !_monitorTestMode &&
                captureBackend == CaptureBackend.Wgc &&
                _wgcSelection is not null &&
                (_gpuLiveOverlayService is not null || !hasSlotOverlay))
            {
                _captureSession.StartLiveWgcCapture();
            }

            StartInternalTimerOverlay();
            if (hasSlotOverlay)
            {
                _liveOverlayTimer.Start();
            }
            UpdateMonitorControlAvailability();
            var clickThroughStatus = _overlayWindow.IsClickThroughConfigured ? "click-through" : "not click-through";
            _log.Info(
                $"Overlay started: size={_layoutCanvasWidth}x{_layoutCanvasHeight}, " +
                $"left={_overlayWindow.Left}, top={_overlayWindow.Top}, opacity={_overlayOpacity}, " +
                $"slots={_overlaySlots.Count}, hotkey={_stopHotkey}, refreshFps={_refreshFps}, " +
                $"captureBackend={CaptureBackendLabel(captureBackend)}, renderer={rendererMode}, " +
                $"refreshMs={_liveOverlayTimer.Interval.TotalMilliseconds}, " +
                $"logPath={_log.LogPath}, " +
                $"exStyle=0x{_overlayWindow.AppliedExtendedStyle.ToInt64():X16}, " +
                $"clickThrough={_overlayWindow.IsClickThroughConfigured}, " +
                $"noActivate={_overlayWindow.IsNoActivateConfigured}, " +
                $"topmost={_overlayWindow.IsTopmostConfigured}, " +
                $"inputHook={_overlayWindow.IsInputHookConfigured}");
            SetStatus(L.F("Overlay started ({0}, {1}, {2}). Stop hotkey: {3}", L.T(clickThroughStatus), L.T(CaptureBackendLabel(captureBackend)), L.T(rendererMode), _stopHotkey));
        }
        catch (Exception ex)
        {
            _log.Error("Overlay start failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus(L.F("Overlay start failed: {0}", ex.Message));
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

    private void CaptureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CaptureCanvas);
        if (_monitorDetectionMode != MonitorDetectionMode.None)
        {
            BeginMonitorDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        if (_isAwaitingDebugDetectionRoi)
        {
            BeginDebugDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        if (_isAwaitingDetectionRoi)
        {
            BeginDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        var hit = _candidates.FirstOrDefault(candidate => GetCandidateVisualRect(candidate).Contains(position));
        if (hit is null)
        {
            BeginCandidateBoxSelection(position);
        }
    }

    private void CaptureCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(CaptureCanvas);
        if (_isSelectingMonitorDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingDebugDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingCandidates)
        {
            UpdateCandidateBoxSelection(position);
            return;
        }

        if (_draggingCandidate is null)
        {
            return;
        }

        if (Math.Abs(position.X - _candidateDragStartPosition.X) > 3 ||
            Math.Abs(position.Y - _candidateDragStartPosition.Y) > 3)
        {
            MoveSelectedCandidates(position);
        }
    }

    private void CaptureCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectingMonitorDetectionRoi)
        {
            EndMonitorDetectionRoiSelection();
            return;
        }

        if (_isSelectingDebugDetectionRoi)
        {
            EndDebugDetectionRoiSelection();
            return;
        }

        if (_isSelectingDetectionRoi)
        {
            EndDetectionRoiSelection();
            return;
        }

        if (_isSelectingCandidates)
        {
            EndCandidateBoxSelection();
            return;
        }

        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            ReleaseCaptureSafely(rect);
        }

        if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
    }

    private void CaptureCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CancelCapturePreviewInteraction(cancelAwaitingModes: true, restoreDragSnapshot: true))
        {
            e.Handled = true;
        }
    }

    private void RefreshWindows()
    {
        var windows = _captureSession.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.LooksLikeMabinogi) ?? windows.FirstOrDefault();
        SetWindowStatusText(BuildWindowStatusText());
    }

    private void SetWindowStatusText(string text)
    {
        WindowStatusText.Text = text;
        WindowStatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string BuildWindowStatusText()
    {
        var captureStatus = CurrentCaptureBackend switch
        {
            CaptureBackend.Wgc => _captureSession.IsWgcSupported ? "WGC supported" : "WGC unavailable",
            CaptureBackend.DxgiDesktopDuplication => "DXGI uses selected window monitor",
            CaptureBackend.GdiBitBlt => "GDI captures selected client area",
            _ => "Capture backend unknown"
        };
        return WindowCombo.SelectedItem is GameWindowInfo selected
            ? selected.LooksLikeMabinogi
                ? string.Empty
                : $"Selected window is not recognized as Mabinogi. ({captureStatus})"
            : "No selectable window found.";
    }

    private void AddCandidateVisual(SlotCandidate candidate)
    {
        var rect = new Rectangle
        {
            Width = GetCandidateVisualRect(candidate).Width,
            Height = GetCandidateVisualRect(candidate).Height,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(45, 30, 144, 255)),
            IsHitTestVisible = true,
            Cursor = Cursors.SizeAll
        };
        rect.MouseLeftButtonDown += (_, args) =>
        {
            BeginCandidateDrag(candidate, rect, args);
            SelectCandidateInList(candidate);
            args.Handled = true;
        };
        rect.LostMouseCapture += (_, _) => CancelInterruptedCaptureInteraction();
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        var visualRect = GetCandidateVisualRect(candidate);
        Canvas.SetLeft(rect, visualRect.X);
        Canvas.SetTop(rect, visualRect.Y);
        UpdateCandidateVisual(candidate);
    }

    private void BeginCandidateDrag(SlotCandidate candidate, Rectangle rect, MouseButtonEventArgs args)
    {
        var isMultiSelectModifierPressed =
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (isMultiSelectModifierPressed)
        {
            candidate.IsSelected = !candidate.IsSelected;
            if (!candidate.IsSelected)
            {
                _draggingCandidate = null;
                return;
            }
        }
        else if (!candidate.IsSelected)
        {
            SetOnlyCandidateSelected(candidate);
        }

        _draggingCandidate = candidate;
        _candidateDragStartPosition = args.GetPosition(CaptureCanvas);
        _candidateDragSnapshotBefore = CaptureCandidateSnapshot();
        _candidateDragOrigins = _candidates
            .Where(item => item.IsSelected)
            .ToDictionary(item => item, item => new Point(item.SourceRect.X, item.SourceRect.Y));
        rect.CaptureMouse();
    }

    private void CancelInterruptedCaptureInteraction()
    {
        if (_isReleasingCaptureIntentionally)
        {
            return;
        }

        CancelCapturePreviewInteraction(cancelAwaitingModes: false, restoreDragSnapshot: false);
    }

    private bool CancelCapturePreviewInteraction(bool cancelAwaitingModes, bool restoreDragSnapshot)
    {
        var hadDetection = _isSelectingDetectionRoi || (cancelAwaitingModes && _isAwaitingDetectionRoi);
        var hadDebugDetection = _isSelectingDebugDetectionRoi || (cancelAwaitingModes && _isAwaitingDebugDetectionRoi);
        var hadMonitorDetection = _isSelectingMonitorDetectionRoi ||
                                  cancelAwaitingModes && _monitorDetectionMode != MonitorDetectionMode.None;
        var hadSelection = _selectionRect is not null ||
                           _isSelectingCandidates ||
                           _isSelectingDetectionRoi ||
                           _isSelectingDebugDetectionRoi ||
                           _isSelectingMonitorDetectionRoi;
        var hadDrag = _draggingCandidate is not null;
        if (!hadSelection && !hadDrag && !hadDetection && !hadDebugDetection && !hadMonitorDetection)
        {
            return false;
        }

        var dragSnapshot = restoreDragSnapshot ? _candidateDragSnapshotBefore : null;

        RemoveSelectionRectangle();
        if (hadDetection)
        {
            _isSelectingDetectionRoi = false;
            SetDetectionMode(active: false);
        }

        if (hadDebugDetection)
        {
            _isSelectingDebugDetectionRoi = false;
            SetDebugDetectionMode(active: false);
        }

        if (hadMonitorDetection)
        {
            _isSelectingMonitorDetectionRoi = false;
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isSelectingCandidates = false;
        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var draggingRect))
        {
            ReleaseCaptureSafely(draggingRect);
        }

        if (dragSnapshot is not null)
        {
            RestoreCandidateSnapshot(dragSnapshot);
        }
        else if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
        ReleaseCaptureSafely(CaptureCanvas);
        SetStatus(hadMonitorDetection
            ? "monitor.detect.canceled"
            : hadDebugDetection
            ? "Debug detect canceled."
            : hadDetection
                ? "Section detection canceled."
                : "Interrupted preview drag was canceled.");
        return true;
    }

    private void RemoveSelectionRectangle()
    {
        if (_selectionRect is null)
        {
            return;
        }

        CaptureCanvas.Children.Remove(_selectionRect);
        _selectionRect = null;
    }

    private void ReleaseCaptureSafely(UIElement element)
    {
        try
        {
            _isReleasingCaptureIntentionally = true;
            if (element.IsMouseCaptured)
            {
                element.ReleaseMouseCapture();
            }
        }
        finally
        {
            _isReleasingCaptureIntentionally = false;
        }
    }

    private void BeginDetectionRoiSelection(Point position)
    {
        _isSelectingDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = CreateProjectAccentBrush(30),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void BeginDebugDetectionRoiSelection(Point position)
    {
        _isSelectingDebugDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = CreateProjectAccentBrush(26),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void BeginMonitorDetectionRoiSelection(Point position)
    {
        _isSelectingMonitorDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = CreateProjectAccentBrush(30),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void UpdateDetectionRoiSelection(Point position) => UpdateSelectionRectangle(position);

    private void EndDetectionRoiSelection()
    {
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetDetectionMode(active: false);

        if (roi.Width < 24 || roi.Height < 24)
        {
            SetStatus("Detection area is too small. Click Detect and drag a full quickslot section area.");
            return;
        }

        var patternKind = ResolveDetectionPatternKind(roi);
        _log.Info(
            $"ROI section detection auto-routed: pattern={patternKind}, roi={roi.X:0},{roi.Y:0},{roi.Width:0}x{roi.Height:0}");
        DetectSectionInRoi(roi, patternKind);
    }

    private void EndDebugDetectionRoiSelection()
    {
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingDebugDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetDebugDetectionMode(active: false);

        if (roi.Width < 24 || roi.Height < 24)
        {
            SetStatus("Debug detection area is too small.");
            return;
        }

        RunDebugDetection(roi, _debugDetectionExpectation);
    }

    private async void EndMonitorDetectionRoiSelection()
    {
        var mode = _monitorDetectionMode;
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingMonitorDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetMonitorDetectionMode(MonitorDetectionMode.None);
        var capturedImage = _capturedImage;
        if (capturedImage is null || roi.Width < 16 || roi.Height < 16)
        {
            SetStatus("monitor.detect.area.too.small");
            return;
        }

        _isMonitorDetectionBusy = true;
        UpdateMonitorControlAvailability();
        SetStatus("monitor.detect.processing");
        try
        {
            if (mode == MonitorDetectionMode.BuffWindow)
            {
                var result = await Task.Run(() => _monitorTemplateDetection.DetectBuffs(capturedImage, roi));
                if (!ReferenceEquals(_capturedImage, capturedImage))
                {
                    SetStatus("monitor.detect.capture.changed");
                    return;
                }
                _buffMonitorRoi = result.Roi;
                _buffIconMatches.Clear();
                foreach (var match in result.Matches)
                {
                    _buffIconMatches[match.NameKey] = match;
                }

                ApplyRecognizedBuffs(result.Matches.Select(match => match.NameKey));
                RefreshMonitorDetectionVisuals();
                foreach (var match in result.Matches)
                {
                    _log.Info(
                        $"Buff template match: key={match.NameKey}, bounds={FormatRect(match.Bounds)}, " +
                        $"structure={match.StructureScore:0.0000}, active={match.IsActive}, stateConfidence={match.StateConfidence:0.0000}");
                }

                SetStatus(result.Matches.Count == 0
                    ? "monitor.buff.detect.none"
                    : L.F("monitor.buff.detect.result", result.Matches.Count));
            }
            else if (mode == MonitorDetectionMode.Tuairim)
            {
                var result = await Task.Run(() => _monitorTemplateDetection.DetectTuairim(capturedImage, roi));
                if (!ReferenceEquals(_capturedImage, capturedImage))
                {
                    SetStatus("monitor.detect.capture.changed");
                    return;
                }
                _tuairimMonitorRoi = roi;
                _tuairimAnchor = result?.Bounds;
                ResetTuairimPercentRecognitionState();
                RefreshMonitorDetectionVisuals();
                if (result is null)
                {
                    SetStatus("monitor.tuairim.detect.none");
                }
                else
                {
                    _log.Info(
                        $"Tuairim template match: bounds={FormatRect(result.Bounds)}, score={result.Score:0.0000}, roi={FormatRect(result.Roi)}");
                    SetStatus(L.F("monitor.tuairim.detect.result", result.Score.ToString("0.000")));
                }

                ScheduleProfileAutoSave();
            }
        }
        catch (Exception exception)
        {
            _log.Error("Monitor template detection failed.", exception);
            SetStatus(L.F("monitor.detect.failed", exception.Message));
        }
        finally
        {
            _isMonitorDetectionBusy = false;
            UpdateMonitorControlAvailability();
        }
    }

    private void ShowMonitorDetectionBounds(IEnumerable<Rect> bounds)
    {
        ClearMonitorDetectionVisuals();
        foreach (var bound in bounds)
        {
            var rectangle = new Rectangle
            {
                Width = bound.Width,
                Height = bound.Height,
                Stroke = CreateProjectAccentBrush(),
                StrokeThickness = 2,
                Fill = CreateProjectAccentBrush(24),
                IsHitTestVisible = false
            };
            _monitorDetectionRects.Add(rectangle);
            CaptureCanvas.Children.Add(rectangle);
            Canvas.SetLeft(rectangle, bound.X);
            Canvas.SetTop(rectangle, bound.Y);
        }
    }

    private void RefreshMonitorDetectionVisuals()
    {
        if (_capturedImage is null)
        {
            ClearMonitorDetectionVisuals();
            return;
        }

        var bounds = _buffIconMatches.Values.Select(match => match.Bounds).ToList();
        if (_tuairimAnchor is Rect tuairimBounds)
        {
            bounds.Add(tuairimBounds);
        }
        ShowMonitorDetectionBounds(bounds);
    }

    private void ClearMonitorDetectionVisuals()
    {
        foreach (var rectangle in _monitorDetectionRects)
        {
            CaptureCanvas.Children.Remove(rectangle);
        }
        _monitorDetectionRects.Clear();
    }

    private static string FormatRect(Rect rect) =>
        $"{rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0}";

    private static OverlayProfileRect? ToProfileRect(Rect? rect) => rect is null
        ? null
        : new OverlayProfileRect
        {
            X = rect.Value.X,
            Y = rect.Value.Y,
            Width = rect.Value.Width,
            Height = rect.Value.Height
        };

    private static Rect? FromProfileRect(OverlayProfileRect? rect) =>
        rect is null || rect.Width <= 0 || rect.Height <= 0
            ? null
            : new Rect(rect.X, rect.Y, rect.Width, rect.Height);

    private void SetDetectionMode(bool active)
    {
        if (active)
        {
            SetDebugDetectionMode(active: false);
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isAwaitingDetectionRoi = active;
        if (DetectModeText is not null)
        {
            DetectModeText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DetectButton is not null)
        {
            DetectButton.Content = active ? L.T("Drag ROI...") : L.T("Auto detect section");
            ApplyDetectModeButtonStyle(DetectButton, active);
        }
    }

    private void SetDebugDetectionMode(bool active)
    {
        if (active)
        {
            SetDetectionMode(active: false);
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isAwaitingDebugDetectionRoi = active;
        if (DebugDetectButton is not null)
        {
            DebugDetectButton.Content = active ? L.T("Drag debug ROI...") : L.T("Debug detect");
            ApplyDetectModeButtonStyle(DebugDetectButton, active);
        }
    }

    private void SetMonitorDetectionMode(MonitorDetectionMode mode)
    {
        if (mode != MonitorDetectionMode.None)
        {
            SetDetectionMode(active: false);
            SetDebugDetectionMode(active: false);
        }

        _monitorDetectionMode = mode;
        UpdateMonitorDetectionButtonPresentation();
        UpdateMonitorControlAvailability();
    }

    private void UpdateMonitorDetectionButtonPresentation()
    {
        if (DetectBuffWindowButton is not null)
        {
            var active = _monitorDetectionMode == MonitorDetectionMode.BuffWindow;
            DetectBuffWindowButton.Content = active ? L.T("Drag ROI...") : L.T("monitor.buff.detect");
            ApplyDetectModeButtonStyle(DetectBuffWindowButton, active);
        }

        if (DetectTuairimUiButton is not null)
        {
            var active = _monitorDetectionMode == MonitorDetectionMode.Tuairim;
            DetectTuairimUiButton.Content = active ? L.T("Drag ROI...") : L.T("monitor.tuairim.detect");
            ApplyDetectModeButtonStyle(DetectTuairimUiButton, active);
        }
    }

    private static SolidColorBrush CreateProjectAccentBrush(byte alpha = 255) =>
        new(Color.FromArgb(alpha, ProjectAccentColor.R, ProjectAccentColor.G, ProjectAccentColor.B));

    private static void ApplyDetectModeButtonStyle(Button button, bool active)
    {
        if (!active)
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            return;
        }

        button.Background = CreateProjectAccentBrush(52);
        button.BorderBrush = CreateProjectAccentBrush();
    }

    private static QuickslotSectionPatternKind ResolveDetectionPatternKind(Rect roi) =>
        roi.Width >= roi.Height
            ? QuickslotSectionPatternKind.TopGrouped
            : QuickslotSectionPatternKind.Vertical;

    private DebugDetectionExpectation? ChooseDebugDetectionExpectation()
    {
        var sectionResult = MessageBox.Show(
            this,
            $"{L.T("Choose the debug detect target.")}\n\n{L.T("Yes: Top grouped 4x2 x3")}\n{L.T("No: Vertical 2x8")}\n{L.T("Cancel: cancel debug detection")}",
            L.T("Debug Detect Target"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (sectionResult == MessageBoxResult.No)
        {
            return DebugDetectionExpectation.Vertical();
        }

        if (sectionResult != MessageBoxResult.Yes)
        {
            return null;
        }

        var topResult = MessageBox.Show(
            this,
            $"{L.T("Choose the top grouped debug target.")}\n\nYes: Top grouped 1 (x=7, y=18)\nNo: Top grouped 2 (x=627, y=18)\n{L.T("Cancel: cancel debug detection")}",
            L.T("Top Grouped Debug Target"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return topResult switch
        {
            MessageBoxResult.Yes => DebugDetectionExpectation.TopGrouped1(),
            MessageBoxResult.No => DebugDetectionExpectation.TopGrouped2(),
            _ => null
        };
    }

    private void DetectSectionInRoi(Rect roi, QuickslotSectionPatternKind patternKind)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var diagnostics = new List<string>();
        var result = _roiSectionDetection.Detect(
            _capturedImage,
            roi,
            patternKind,
            diagnostics);
        var logPath = SaveDetectLog(roi, patternKind, result, diagnostics);

        if (result is null)
        {
            _log.Info($"ROI section detection failed. Log: {logPath}");
            SetStatus(L.F("No matching quickslot section pattern was found in the selected area. Log: {0}", logPath));
            return;
        }

        AddDetectedSection(result);
        PushUndoIfChanged(before);

        _log.Info(
            $"ROI section detection completed: pattern={patternKind}, roi={roi.X:0},{roi.Y:0},{roi.Width:0}x{roi.Height:0}, " +
            $"slots={result.Slots.Count}, gapX={result.SmallGapX:0}, gapY={result.SmallGapY:0}, largeGap={result.LargeGap:0}, score={result.Score:0.00}, log={logPath}");
        SetStatus(L.F(
            "Added {0}: {1} slots, slot {2}x{3}px, gap X {4}px, gap Y {5}px, large gap {6}px. Log: {7}",
            L.T(GetSectionPatternName(PatternIndexFromKind(patternKind))),
            result.Slots.Count,
            result.Slots[0].Width.ToString("0"),
            result.Slots[0].Height.ToString("0"),
            result.SmallGapX.ToString("0"),
            result.SmallGapY.ToString("0"),
            result.LargeGap.ToString("0"),
            logPath));
    }

    private void RunDebugDetection(Rect roi, DebugDetectionExpectation expected)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var expectedGapText = expected.LargeGap is int expectedLargeGap
            ? $"gapX={expected.SmallGapX}, gapY={expected.SmallGapY}, large={expectedLargeGap}"
            : $"gapX={expected.SmallGapX}, gapY={expected.SmallGapY}";

        var lines = new List<string>
        {
            $"Debug {expected.Label} ROI detect started {DateTimeOffset.Now:O}",
            $"roi absolute x={roi.X:0.###}, y={roi.Y:0.###}, w={roi.Width:0.###}, h={roi.Height:0.###}",
            $"expected absolute x={expected.AbsoluteX}, y={expected.AbsoluteY}, {expected.Size}x{expected.Size}, {expectedGapText}",
            $"runs={DebugDetectRuns}"
        };

        var exactMatches = 0;
        var detections = 0;
        for (var run = 1; run <= DebugDetectRuns; run++)
        {
            var diagnostics = new List<string>();
            var result = _roiSectionDetection.Detect(
                _capturedImage,
                roi,
                expected.PatternKind,
                diagnostics);

            if (result is null)
            {
                lines.Add($"RUN {run:000}: FAIL no result");
                lines.AddRange(diagnostics.Select(line => $"  {line}"));
                continue;
            }

            detections++;
            var first = result.Slots[0];
            var absoluteX = (int)Math.Round(first.X);
            var absoluteY = (int)Math.Round(first.Y);
            var relativeX = (int)Math.Round(first.X - roi.X);
            var relativeY = (int)Math.Round(first.Y - roi.Y);
            var width = (int)Math.Round(first.Width);
            var height = (int)Math.Round(first.Height);
            var gapX = (int)Math.Round(result.SmallGapX);
            var gapY = (int)Math.Round(result.SmallGapY);
            var largeGap = (int)Math.Round(result.LargeGap);
            var isExact =
                absoluteX == expected.AbsoluteX &&
                absoluteY == expected.AbsoluteY &&
                width == expected.Size &&
                height == expected.Size &&
                gapX == expected.SmallGapX &&
                gapY == expected.SmallGapY &&
                (expected.LargeGap is null || largeGap == expected.LargeGap.Value);

            if (isExact)
            {
                exactMatches++;
                continue;
            }

            lines.Add(
                $"RUN {run:000}: WRONG abs x={absoluteX}, y={absoluteY}, rel x={relativeX}, y={relativeY}, {width}x{height}, gapX={gapX}, gapY={gapY}, large={largeGap}, score={result.Score:0.000}");
            lines.Add(
                $"  abs x={first.X:0.###}, y={first.Y:0.###}, w={first.Width:0.###}, h={first.Height:0.###}");
            lines.AddRange(diagnostics.Select(line => $"  {line}"));
        }

        lines.Insert(4, $"summary exact={exactMatches}/{DebugDetectRuns}, detected={detections}/{DebugDetectRuns}, failed={DebugDetectRuns - detections}");
        var logPath = SaveDebugDetectLog(lines);
        _log.Info($"Debug detect log saved: {logPath}");
        SetStatus(L.F(
            "Debug detect finished: exact {0}/{1}, detected {2}/{3}. Log: {4}",
            exactMatches,
            DebugDetectRuns,
            detections,
            DebugDetectRuns,
            logPath));
    }

    private sealed record DebugDetectionExpectation(
        string Label,
        QuickslotSectionPatternKind PatternKind,
        int AbsoluteX,
        int AbsoluteY,
        int Size,
        int SmallGapX,
        int SmallGapY,
        int? LargeGap)
    {
        public static DebugDetectionExpectation TopGrouped1() =>
            new("top grouped 1 4x2 x3", QuickslotSectionPatternKind.TopGrouped, 7, 18, 29, 3, 8, 15);

        public static DebugDetectionExpectation TopGrouped2() =>
            new("top grouped 2 4x2 x3", QuickslotSectionPatternKind.TopGrouped, 627, 18, 29, 3, 8, 15);

        public static DebugDetectionExpectation Vertical() =>
            new("vertical 2x8", QuickslotSectionPatternKind.Vertical, 91, 417, 29, 3, 3, null);
    }

    private string SaveDebugDetectLog(IReadOnlyCollection<string> lines)
    {
        var path = System.IO.Path.Combine(
            _log.LogDirectory,
            $"detect-debug-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        System.IO.Directory.CreateDirectory(_log.LogDirectory);
        System.IO.File.WriteAllLines(path, lines);
        return path;
    }

    private string SaveDetectLog(
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        SectionDetectionResult? result,
        IReadOnlyCollection<string> diagnostics)
    {
        var lines = new List<string>
        {
            $"Detect ROI started {DateTimeOffset.Now:O}",
            $"pattern={patternKind}",
            $"roi absolute x={roi.X:0.###}, y={roi.Y:0.###}, w={roi.Width:0.###}, h={roi.Height:0.###}"
        };

        if (_capturedImage is not null)
        {
            lines.Add($"capture image {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}");
        }

        if (result is null)
        {
            lines.Add("result=FAIL no matching quickslot section pattern");
        }
        else
        {
            var first = result.Slots[0];
            lines.Add(
                $"result=OK slots={result.Slots.Count}, first x={first.X:0.###}, y={first.Y:0.###}, w={first.Width:0.###}, h={first.Height:0.###}, " +
                $"gapX={result.SmallGapX:0.###}, gapY={result.SmallGapY:0.###}, large={result.LargeGap:0.###}, score={result.Score:0.000}");
        }

        lines.AddRange(diagnostics.Select(line => $"  {line}"));
        lines.Add(string.Empty);

        lock (_detectLogSync)
        {
            System.IO.Directory.CreateDirectory(_log.LogDirectory);
            var isNewLog = !System.IO.File.Exists(_detectSessionLogPath);
            var output = new List<string>();
            if (isNewLog)
            {
                output.Add($"Detect session log started {DateTimeOffset.Now:O}");
                output.Add(string.Empty);
            }

            output.Add("----");
            output.AddRange(lines);
            System.IO.File.AppendAllLines(_detectSessionLogPath, output);
        }

        return _detectSessionLogPath;
    }

    private void BeginCandidateBoxSelection(Point position)
    {
        _isSelectingCandidates = true;
        _selectionStartPosition = position;
        var isMultiSelectModifierPressed =
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!isMultiSelectModifierPressed)
        {
            ClearCandidateSelection();
        }

        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 1,
            Fill = CreateProjectAccentBrush(35),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void UpdateCandidateBoxSelection(Point position)
    {
        UpdateSelectionRectangle(position);
    }

    private void UpdateSelectionRectangle(Point position)
    {
        if (_selectionRect is null)
        {
            return;
        }

        var left = Math.Min(_selectionStartPosition.X, position.X);
        var top = Math.Min(_selectionStartPosition.Y, position.Y);
        var width = Math.Abs(position.X - _selectionStartPosition.X);
        var height = Math.Abs(position.Y - _selectionStartPosition.Y);
        Canvas.SetLeft(_selectionRect, left);
        Canvas.SetTop(_selectionRect, top);
        _selectionRect.Width = width;
        _selectionRect.Height = height;
    }

    private void EndCandidateBoxSelection()
    {
        if (_selectionRect is not null)
        {
            var selection = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            foreach (var candidate in _candidates)
            {
                if (selection.IntersectsWith(GetCandidateVisualRect(candidate)))
                {
                    candidate.IsSelected = true;
                }
            }

            RemoveSelectionRectangle();
        }

        _isSelectingCandidates = false;
        ReleaseCaptureSafely(CaptureCanvas);
    }

    private void MoveSelectedCandidates(Point position)
    {
        if (_candidateDragOrigins.Count == 0)
        {
            return;
        }

        var requestedDeltaX = position.X - _candidateDragStartPosition.X;
        var requestedDeltaY = position.Y - _candidateDragStartPosition.Y;
        var minDeltaX = _candidateDragOrigins.Max(item => CandidateBorderPixels - item.Value.X);
        var minDeltaY = _candidateDragOrigins.Max(item => CandidateBorderPixels - item.Value.Y);
        var maxDeltaX = _candidateDragOrigins.Min(item => CaptureCanvas.Width - CandidateBorderPixels - item.Value.X - item.Key.SourceRect.Width);
        var maxDeltaY = _candidateDragOrigins.Min(item => CaptureCanvas.Height - CandidateBorderPixels - item.Value.Y - item.Key.SourceRect.Height);
        var deltaX = Math.Clamp(requestedDeltaX, minDeltaX, maxDeltaX);
        var deltaY = Math.Clamp(requestedDeltaY, minDeltaY, maxDeltaY);

        foreach (var (candidate, origin) in _candidateDragOrigins)
        {
            var x = origin.X + deltaX;
            var y = origin.Y + deltaY;
            MoveCandidate(candidate, x, y);
        }
    }

    private void MoveCandidate(SlotCandidate candidate, double x, double y)
    {
        _candidateWorkspace.MoveCandidate(
            candidate,
            x,
            y,
            CaptureCanvas.Width,
            CaptureCanvas.Height,
            CandidateBorderPixels);
        UpdateCandidateVisualPosition(candidate);
    }

    private void UpdateCandidateVisualPosition(SlotCandidate candidate)
    {
        if (_candidateRects.TryGetValue(candidate, out var candidateRect))
        {
            var visualRect = GetCandidateVisualRect(candidate);
            candidateRect.Width = visualRect.Width;
            candidateRect.Height = visualRect.Height;
            Canvas.SetLeft(candidateRect, visualRect.X);
            Canvas.SetTop(candidateRect, visualRect.Y);
        }
    }

    private void SetOnlyCandidateSelected(SlotCandidate selected)
        => _candidateWorkspace.SelectOnly(selected);

    private void ClearCandidateSelection()
        => _candidateWorkspace.ClearSelection();

    private int AddSectionCandidates(SlotCandidate seed, SectionPattern pattern, int patternIndex, SectionSettings settings)
    {
        var added = 0;
        var sectionCandidates = new List<SlotCandidate>();

        ClearCandidateSelection();
        foreach (var offset in BuildSectionOffsets(seed, pattern, settings.SmallGapX, settings.SmallGapY, settings.LargeGap))
        {
            var rect = new Rect(
                seed.SourceRect.X + offset.X,
                seed.SourceRect.Y + offset.Y,
                seed.SourceRect.Width,
                seed.SourceRect.Height);
            if (!IsRectInsideCapture(rect))
            {
                continue;
            }

            var existing = FindMatchingCandidate(rect);
            if (existing is not null)
            {
                existing.IsSelected = true;
                sectionCandidates.Add(existing);
                continue;
            }

            var candidate = new SlotCandidate(NextCandidateId(), rect, 200);
            candidate.IsSelected = true;
            AddCandidate(candidate);
            sectionCandidates.Add(candidate);
            added++;
        }

        CandidateList.SelectedItem = seed;
        var section = _candidateWorkspace.AddSection(seed, patternIndex, settings, sectionCandidates);
        SelectSection(section);
        return added;
    }

    private void AddDetectedSection(SectionDetectionResult result)
    {
        var patternIndex = PatternIndexFromKind(result.PatternKind);
        var settings = new SectionSettings(result.SmallGapX, result.SmallGapY, result.LargeGap);
        var sectionCandidates = new List<SlotCandidate>();

        ClearCandidateSelection();
        foreach (var rect in result.Slots)
        {
            var candidate = FindMatchingCandidate(rect);
            if (candidate is null)
            {
                candidate = new SlotCandidate(NextCandidateId(), rect, result.Score);
                AddCandidate(candidate);
            }

            candidate.IsSelected = true;
            sectionCandidates.Add(candidate);
        }

        if (sectionCandidates.Count == 0)
        {
            return;
        }

        var seed = sectionCandidates
            .OrderBy(candidate => candidate.SourceRect.Y)
            .ThenBy(candidate => candidate.SourceRect.X)
            .First();
        CandidateList.SelectedItem = seed;
        _sectionSettings[patternIndex] = settings;
        var section = _candidateWorkspace.AddSection(seed, patternIndex, settings, sectionCandidates);
        SelectSection(section);
    }

    private static IEnumerable<Point> BuildSectionOffsets(
        SlotCandidate seed,
        SectionPattern pattern,
        double smallGap,
        double smallGapY,
        double largeGap)
    {
        var slotWidth = seed.SourceRect.Width;
        var slotHeight = seed.SourceRect.Height;
        var smallGapX = pattern.InnerGapX(smallGap);
        var smallGapYValue = pattern.InnerGapY(smallGapY);
        var innerPitchX = slotWidth + smallGapX;
        var innerPitchY = slotHeight + smallGapYValue;
        var groupPitchX = (pattern.GroupColumns * slotWidth) +
                          (Math.Max(0, pattern.GroupColumns - 1) * smallGapX) +
                          pattern.GroupGapX(largeGap);
        var groupPitchY = (pattern.GroupRows * slotHeight) +
                          (Math.Max(0, pattern.GroupRows - 1) * smallGapYValue) +
                          pattern.GroupGapY(largeGap);

        for (var groupY = 0; groupY < pattern.GroupRowsCount; groupY++)
        {
            for (var groupX = 0; groupX < pattern.GroupColumnsCount; groupX++)
            {
                for (var row = 0; row < pattern.GroupRows; row++)
                {
                    for (var column = 0; column < pattern.GroupColumns; column++)
                    {
                        yield return new Point(
                            groupX * groupPitchX + column * innerPitchX,
                            groupY * groupPitchY + row * innerPitchY);
                    }
                }
            }
        }
    }

    private SlotCandidate? FindMatchingCandidate(Rect rect)
    {
        var tolerance = Math.Max(2, rect.Width * 0.18);
        return _candidates.FirstOrDefault(candidate =>
            Math.Abs(candidate.SourceRect.X - rect.X) <= tolerance &&
            Math.Abs(candidate.SourceRect.Y - rect.Y) <= tolerance &&
            Math.Abs(candidate.SourceRect.Width - rect.Width) <= tolerance &&
            Math.Abs(candidate.SourceRect.Height - rect.Height) <= tolerance);
    }

    private bool IsRectInsideCapture(Rect rect) =>
        rect.X >= CandidateBorderPixels &&
        rect.Y >= CandidateBorderPixels &&
        rect.Right <= CaptureCanvas.Width - CandidateBorderPixels &&
        rect.Bottom <= CaptureCanvas.Height - CandidateBorderPixels;

    private static Rect GetCandidateVisualRect(SlotCandidate candidate)
    {
        var rect = candidate.SourceRect;
        rect.Inflate(CandidateVisualPaddingPixels, CandidateVisualPaddingPixels);
        return rect;
    }

    private SectionPattern ReadSectionPattern() =>
        GetSectionPattern(Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1));

    private static SectionPattern GetSectionPattern(int index) =>
        index == 1 ? SectionPattern.Vertical() : SectionPattern.TopGrouped();

    private static int PatternIndexFromKind(QuickslotSectionPatternKind kind) =>
        kind == QuickslotSectionPatternKind.Vertical ? 1 : 0;

    private void RebuildSelectedSection()
    {
        if (_selectedSection is null || _capturedImage is null || _isUpdatingSectionControls)
        {
            return;
        }

        _selectedSection.Settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        var pattern = GetSectionPattern(_selectedSection.PatternIndex);
        var offsets = BuildSectionOffsets(
                _selectedSection.Seed,
                pattern,
                _selectedSection.Settings.SmallGapX,
                _selectedSection.Settings.SmallGapY,
                _selectedSection.Settings.LargeGap)
            .ToList();
        var count = Math.Min(offsets.Count, _selectedSection.Candidates.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = _selectedSection.Candidates[i];
            var offset = offsets[i];
            var x = _selectedSection.Seed.SourceRect.X + offset.X;
            var y = _selectedSection.Seed.SourceRect.Y + offset.Y;
            MoveCandidate(candidate, x, y);
        }

        RefreshSectionLabels();
        SetStatus(L.F(
            "Adjusted {0}: gap X {1}px, gap Y {2}px, large gap {3}px.",
            _selectedSection.Label,
            ReadSmallGapX().ToString("0"),
            ReadSmallGapY().ToString("0"),
            ReadLargeGap().ToString("0")));
    }

    private bool TryNudgeSelectedCandidates(Key key)
    {
        var delta = key switch
        {
            Key.Left => new Vector(-1, 0),
            Key.Right => new Vector(1, 0),
            Key.Up => new Vector(0, -1),
            Key.Down => new Vector(0, 1),
            _ => default
        };
        if (delta == default)
        {
            return false;
        }

        Keyboard.ClearFocus();
        CaptureCanvas.Focus();
        var selected = _candidates.Where(candidate => candidate.IsSelected && !candidate.IsBuiltIn).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate { IsBuiltIn: false } highlighted)
        {
            selected.Add(highlighted);
        }

        if (selected.Count == 0)
        {
            return false;
        }

        var before = CaptureCandidateSnapshot();
        foreach (var candidate in selected)
        {
            MoveCandidate(
                candidate,
                candidate.SourceRect.X + delta.X,
                candidate.SourceRect.Y + delta.Y);
        }
        PushUndoIfChanged(before);

        SetStatus(L.F("Nudged {0} candidate(s) by 1px.", selected.Count));
        return true;
    }

    private void AddCandidate(SlotCandidate candidate)
    {
        _candidates.Add(candidate);
        candidate.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SlotCandidate.IsSelected))
            {
                UpdateCandidateVisual(candidate);
                ScheduleProfileAutoSave();
            }
        };
        if (!candidate.IsBuiltIn)
        {
            AddCandidateVisual(candidate);
        }
    }

    private SlotCandidate EnsureInternalTimerCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.InternalBuffTimer);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                InternalBuffTimerPreviewRenderer.BaseWidth,
                InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count));
            return existing;
        }

        var baseHeight = InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count);
        var candidate = new SlotCandidate(
            -1,
            new Rect(0, 0, InternalBuffTimerPreviewRenderer.BaseWidth, baseHeight),
            100,
            OverlayElementKind.InternalBuffTimer,
            "monitor.timer.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private SlotCandidate EnsureTuairimGaugeCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.TuairimGauge);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                TuairimGaugePreviewRenderer.BaseWidth,
                TuairimGaugePreviewRenderer.BaseHeight);
            return existing;
        }

        var candidate = new SlotCandidate(
            -2,
            new Rect(0, 0, TuairimGaugePreviewRenderer.BaseWidth, TuairimGaugePreviewRenderer.BaseHeight),
            100,
            OverlayElementKind.TuairimGauge,
            "monitor.tuairim.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private SlotCandidate EnsureAlertNotificationCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.AlertNotification);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                AlertNotificationPreviewRenderer.BaseWidth,
                AlertNotificationPreviewRenderer.BaseHeight);
            return existing;
        }

        var candidate = new SlotCandidate(
            -3,
            new Rect(0, 0, AlertNotificationPreviewRenderer.BaseWidth, AlertNotificationPreviewRenderer.BaseHeight),
            100,
            OverlayElementKind.AlertNotification,
            "monitor.alert.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private void SynchronizeMonitorElementDimensions()
    {
        var timerCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.InternalBuffTimer);
        if (timerCandidate is not null)
        {
            ResizeMonitorElement(
                timerCandidate,
                InternalBuffTimerPreviewRenderer.BaseWidth,
                InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count));
        }

        var tuairimCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.TuairimGauge);
        if (tuairimCandidate is not null)
        {
            ResizeMonitorElement(
                tuairimCandidate,
                TuairimGaugePreviewRenderer.BaseWidth,
                TuairimGaugePreviewRenderer.BaseHeight);
        }

        var alertCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.AlertNotification);
        if (alertCandidate is not null)
        {
            ResizeMonitorElement(
                alertCandidate,
                AlertNotificationPreviewRenderer.BaseWidth,
                AlertNotificationPreviewRenderer.BaseHeight);
        }
    }

    private void ResizeMonitorElement(SlotCandidate candidate, double width, double height)
    {
        var oldWidth = candidate.SourceRect.Width;
        var oldHeight = candidate.SourceRect.Height;
        if (Math.Abs(oldWidth - width) < 0.01 && Math.Abs(oldHeight - height) < 0.01)
        {
            return;
        }

        var slot = _overlaySlots.FirstOrDefault(item => item.Kind == candidate.Kind);
        if (slot is not null)
        {
            var scale = oldWidth > 0 && oldHeight > 0
                ? Math.Min(slot.OverlayRect.Width / oldWidth, slot.OverlayRect.Height / oldHeight)
                : Math.Max(0.1, slot.Scale);
            if (candidate.Kind == OverlayElementKind.InternalBuffTimer && Math.Abs(oldWidth - width) < 0.01)
            {
                slot.OverlayRect = new Rect(
                    slot.OverlayRect.X,
                    slot.OverlayRect.Y,
                    slot.OverlayRect.Width,
                    Math.Max(MinimumOverlaySlotSize, height * slot.OverlayRect.Width / width));
            }
            else
            {
                slot.OverlayRect = new Rect(
                    slot.OverlayRect.X,
                    slot.OverlayRect.Y,
                    Math.Max(MinimumOverlaySlotSize, width * scale),
                    Math.Max(MinimumOverlaySlotSize, height * scale));
            }
        }

        candidate.ResizeTo(width, height);
    }

    private void SetMonitorElementEnabled(OverlayElementKind kind, bool enabled, bool scheduleAutoSave = true)
    {
        if (enabled)
        {
            EnsureMonitorElementPlaced(kind, scheduleAutoSave: false);
        }
        else
        {
            var candidates = _candidates.Where(candidate => candidate.Kind == kind).ToList();
            _overlaySlots.RemoveAll(slot => slot.Kind == kind);
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(CandidateList.SelectedItem, candidate))
                {
                    CandidateList.SelectedItem = null;
                }
                _candidates.Remove(candidate);
            }

            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
        }

        if (scheduleAutoSave)
        {
            ScheduleProfileAutoSave();
        }
    }

    private void EnsureEnabledMonitorElementsPlaced(bool scheduleAutoSave = false)
    {
        if (_buffMonitorEnabled)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.InternalBuffTimer, scheduleAutoSave);
        }
        if (_tuairimMonitorEnabled)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.TuairimGauge, scheduleAutoSave);
        }
        if (_buffMonitorEnabled || _tuairimMonitorEnabled)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.AlertNotification, scheduleAutoSave);
        }
    }

    private void SynchronizeAlertNotificationElement(bool scheduleAutoSave = true) =>
        SetMonitorElementEnabled(
            OverlayElementKind.AlertNotification,
            _buffMonitorEnabled || _tuairimMonitorEnabled,
            scheduleAutoSave);

    private bool IsMonitorElementEnabled(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => _buffMonitorEnabled,
        OverlayElementKind.TuairimGauge => _tuairimMonitorEnabled,
        OverlayElementKind.AlertNotification => _buffMonitorEnabled || _tuairimMonitorEnabled,
        _ => false
    };

    private void EnsureMonitorElementPlaced(OverlayElementKind kind, bool scheduleAutoSave = true)
    {
        var candidate = kind switch
        {
            OverlayElementKind.InternalBuffTimer => EnsureInternalTimerCandidate(),
            OverlayElementKind.TuairimGauge => EnsureTuairimGaugeCandidate(),
            OverlayElementKind.AlertNotification => EnsureAlertNotificationCandidate(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only monitor elements can be auto-placed.")
        };
        var existingSlot = _overlaySlots.FirstOrDefault(slot => slot.Kind == kind);
        if (existingSlot is not null)
        {
            existingSlot.Source = candidate;
            existingSlot.Preview = RenderMonitorElementPreview(kind);
            UpdateCandidateOverlayFlags();
            return;
        }

        var scale = ReadLayoutSlotScale();
        var width = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Width * scale);
        var height = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Height * scale);
        var x = 8.0;
        var y = _overlaySlots.Count == 0 ? 8.0 : _overlaySlots.Max(slot => slot.OverlayRect.Bottom) + 8;
        _overlaySlots.Add(new OverlaySlot(
            candidate,
            new Rect(x, y, width, height),
            RenderMonitorElementPreview(kind),
            scale: scale));
        _layoutCanvasHeight = Math.Max(_layoutCanvasHeight, y + height + 8);
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        if (scheduleAutoSave)
        {
            ScheduleProfileAutoSave();
        }
    }

    private BitmapSource RenderMonitorElementPreview(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => InternalBuffTimerPreviewRenderer.Render(
            _internalBuffTimers,
            _selectedBuffNameKeys),
        OverlayElementKind.TuairimGauge => TuairimGaugePreviewRenderer.Render(_tuairimPercent),
        OverlayElementKind.AlertNotification => AlertNotificationPreviewRenderer.Render(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A quickslot requires a captured image crop.")
    };

    private void UpdateCandidateVisual(SlotCandidate candidate)
    {
        if (!_candidateRects.TryGetValue(candidate, out var rect))
        {
            return;
        }

        rect.Stroke = candidate.IsSelected ? Brushes.LimeGreen : Brushes.OrangeRed;
        rect.Fill = candidate.IsSelected
            ? new SolidColorBrush(Color.FromArgb(55, 50, 205, 50))
            : new SolidColorBrush(Color.FromArgb(35, 255, 80, 80));
    }

    private void SelectCandidateInList(SlotCandidate candidate)
    {
        CandidateList.SelectedItem = candidate;
        CandidateList.ScrollIntoView(candidate);
        CandidateList.Focus();
    }

    private void ClearLayout()
    {
        _overlaySlots.Clear();
        EnsureEnabledMonitorElementsPlaced();
        UpdateLayoutSummary();
        UpdateCandidateOverlayFlags();
    }

    private int RemoveOverlaySlotsForCandidates(IEnumerable<SlotCandidate> candidates)
        => _candidateWorkspace.RemoveOverlaySlotsForCandidates(candidates);

    private void UpdateCandidateOverlayFlags()
        => _candidateWorkspace.UpdateCandidateOverlayFlags();

    private void SelectSection(QuickslotSection section)
    {
        _selectedSection = section;
        _isUpdatingSectionSelection = true;
        try
        {
            SectionCombo.SelectedItem = section;
        }
        finally
        {
            _isUpdatingSectionSelection = false;
        }

        SelectSectionCandidates(section);
        LoadSectionControls(section);
        RefreshSectionLabels();
    }

    private void SelectSectionCandidates(QuickslotSection section)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = section.Candidates.Contains(candidate);
        }

        CandidateList.SelectedItem = section.Seed;
    }

    private void LoadSectionControls(QuickslotSection section)
    {
        _currentSectionIndex = Math.Clamp(section.PatternIndex, 0, _sectionSettings.Length - 1);
        _sectionSettings[_currentSectionIndex] = section.Settings;
        _isUpdatingSectionControls = true;
        try
        {
            SectionPatternCombo.SelectedIndex = _currentSectionIndex;
            SmallGapXSlider.Value = Math.Clamp(section.Settings.SmallGapX, SmallGapXSlider.Minimum, SmallGapXSlider.Maximum);
            SmallGapYSlider.Value = Math.Clamp(section.Settings.SmallGapY, SmallGapYSlider.Minimum, SmallGapYSlider.Maximum);
            LargeGapSlider.Value = Math.Clamp(section.Settings.LargeGap, LargeGapSlider.Minimum, LargeGapSlider.Maximum);
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }

        UpdateSectionGapLabels();
    }

    private void ClearSections()
    {
        _candidateWorkspace.ClearSections();
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
    }

    private void RemoveSectionsContaining(IReadOnlyCollection<SlotCandidate> candidates)
    {
        if (_candidateWorkspace.RemoveSectionsContaining(candidates))
        {
            SectionCombo.SelectedItem = null;
        }

        RefreshSectionLabels();
    }

    private void RefreshSectionLabels()
    {
        _candidateWorkspace.RefreshSectionMemberships();

        SectionCombo.Items.Refresh();
    }

    private void ClearCandidateRects()
    {
        foreach (var rect in _candidateRects.Values)
        {
            CaptureCanvas.Children.Remove(rect);
        }

        _candidateRects.Clear();
    }

    private bool TryHandleUndoRedo(KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return false;
        }

        if (e.Key == Key.Z)
        {
            UndoCandidateEdit();
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.Y)
        {
            RedoCandidateEdit();
            e.Handled = true;
            return true;
        }

        return false;
    }

    private CandidateEditSnapshot CaptureCandidateSnapshot()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        return _candidateWorkspace.CaptureSnapshot(selectedId);
    }

    private void PushUndoIfChanged(CandidateEditSnapshot before)
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.PushUndoIfChanged(before, selectedId))
        {
            return;
        }

        ScheduleProfileAutoSave();
    }

    private void UndoCandidateEdit()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.TryUndo(selectedId, out var previous))
        {
            SetStatus("No candidate edit to undo.");
            return;
        }

        RestoreCandidateSnapshot(previous);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit undone.");
    }

    private void RedoCandidateEdit()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.TryRedo(selectedId, out var next))
        {
            SetStatus("No candidate edit to redo.");
            return;
        }

        RestoreCandidateSnapshot(next);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit redone.");
    }

    private void RestoreCandidateSnapshot(CandidateEditSnapshot snapshot)
    {
        ClearCandidateRects();
        var restored = _candidateWorkspace.RestoreSnapshot(
            snapshot,
            kind => kind == OverlayElementKind.Quickslot || IsMonitorElementEnabled(kind));
        foreach (var candidate in _candidates)
        {
            AddCandidateVisual(candidate);
        }

        var restoredById = restored.CandidatesById.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (_buffMonitorEnabled)
        {
            var internalTimerCandidate = EnsureInternalTimerCandidate();
            restoredById[internalTimerCandidate.Id] = internalTimerCandidate;
        }
        if (_tuairimMonitorEnabled)
        {
            var tuairimGaugeCandidate = EnsureTuairimGaugeCandidate();
            restoredById[tuairimGaugeCandidate.Id] = tuairimGaugeCandidate;
        }
        if (_buffMonitorEnabled || _tuairimMonitorEnabled)
        {
            var alertCandidate = EnsureAlertNotificationCandidate();
            restoredById[alertCandidate.Id] = alertCandidate;
        }

        CandidateList.SelectedItem = restored.SelectedCandidate;
        if (restored.SelectedSection is not null)
        {
            SelectSection(restored.SelectedSection);
        }
        else
        {
            SectionCombo.SelectedItem = null;
        }

        foreach (var slot in _overlaySlots.ToList())
        {
            if (!restoredById.TryGetValue(slot.Source.Id, out var restoredSource))
            {
                _overlaySlots.Remove(slot);
                continue;
            }

            slot.Source = restoredSource;
            if (restoredSource.Kind != OverlayElementKind.Quickslot)
            {
                slot.Preview = RenderMonitorElementPreview(restoredSource.Kind);
            }
            else if (_capturedImage is not null)
            {
                slot.Preview = _captureSession.Crop(_capturedImage, restoredSource.SourceRect);
            }
        }

        UpdateLayoutSummary();
    }

    private int NextCandidateId() => _candidateWorkspace.NextCandidateId();

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
        _internalTimerDebugTimer.Stop();
        _pendingInitialBuffMinuteValidation.Clear();
        ResetTuairimPercentRecognitionState();
        _monitorValueRecognitionGeneration++;
        LogCpuRenderStats(final: true);
        _gpuLiveOverlayService?.Dispose();
        _gpuLiveOverlayService = null;
        _captureSession.StopLiveWgcCapture();
        _overlayWindow?.Close();
        _overlayWindow = null;
        _internalTimerOverlayWindow?.Close();
        _internalTimerOverlayWindow = null;
        _hotkeyService?.Dispose();
        _hotkeyService = null;
        UpdateMonitorControlAvailability();
        if (setStatus)
        {
            _log.Info("Overlay stopped.");
            SetStatus("Overlay stopped.");
        }
    }

    private void ResetCpuRenderStats()
    {
        _cpuRenderClock.Reset();
        _cpuStatsLastLogTicks = Stopwatch.GetTimestamp();
        _cpuStatsTicks = 0;
        _cpuStatsMaxTicks = 0;
        _cpuStatsFrames = 0;
        _cpuStatsSkippedBusy = 0;
        _cpuStatsErrors = 0;
    }

    private void RecordCpuRenderFrame(OverlayRenderMode mode, long elapsedTicks)
    {
        if (mode == OverlayRenderMode.GpuDxgi)
        {
            return;
        }

        _cpuStatsFrames++;
        _cpuStatsTicks += elapsedTicks;
        _cpuStatsMaxTicks = Math.Max(_cpuStatsMaxTicks, elapsedTicks);

        var now = Stopwatch.GetTimestamp();
        if ((now - _cpuStatsLastLogTicks) / (double)Stopwatch.Frequency >= 5)
        {
            LogCpuRenderStats(final: false);
            _cpuStatsLastLogTicks = now;
        }
    }

    private void LogCpuRenderStats(bool final)
    {
        if (_activeRenderMode == OverlayRenderMode.GpuDxgi || _cpuStatsFrames == 0)
        {
            return;
        }

        var averageMs = _cpuStatsTicks * 1000.0 / Stopwatch.Frequency / _cpuStatsFrames;
        var maxMs = _cpuStatsMaxTicks * 1000.0 / Stopwatch.Frequency;
        _log.Info(
            $"CPU renderer stats{(final ? " final" : string.Empty)}: mode={RenderModeLabel(_activeRenderMode)}, " +
            $"frames={_cpuStatsFrames}, avgMs={averageMs:0.00}, maxMs={maxMs:0.00}, " +
            $"skippedBusy={_cpuStatsSkippedBusy}, errors={_cpuStatsErrors}, slots={_overlaySlots.Count}");
    }

    private void LiveOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if (!HasLiveCaptureSource() ||
            _overlayWindow is null ||
            _overlaySlots.Count == 0 ||
            _isLiveRefreshInProgress)
        {
            if (_isLiveRefreshInProgress)
            {
                _cpuStatsSkippedBusy++;
            }

            return;
        }

        try
        {
            _isLiveRefreshInProgress = true;
            _cpuRenderClock.Restart();
            if (_gpuLiveOverlayService is not null)
            {
                if (_gpuLiveOverlayService.LastException is not null)
                {
                    throw new InvalidOperationException("GPU live overlay renderer failed.", _gpuLiveOverlayService.LastException);
                }

                return;
            }

            BitmapSource liveCapture;
            var captureBackend = CurrentCaptureBackend;
            if (captureBackend == CaptureBackend.Wgc)
            {
                if (_captureSession.LastLiveCaptureException is not null)
                {
                    throw new InvalidOperationException("Live WGC capture failed.", _captureSession.LastLiveCaptureException);
                }

                if (!_captureSession.TryGetLatestWgcFrame(out var latestFrame) || latestFrame is null)
                {
                    return;
                }

                liveCapture = latestFrame;
            }
            else
            {
                liveCapture = _captureSession.CaptureCurrentFrame(captureBackend)
                              ?? throw new InvalidOperationException("The selected capture source is unavailable.");
            }

            if (_activeRenderMode == OverlayRenderMode.CpuComposited)
            {
                var compositedFrame = _cpuCompositedRenderer.Render(
                    liveCapture,
                    _overlaySlots,
                    (int)Math.Ceiling(_layoutCanvasWidth),
                    (int)Math.Ceiling(_layoutCanvasHeight),
                    _overlayOpacity);
                _overlayWindow.RenderCompositedFrame(compositedFrame);
            }
            else
            {
                foreach (var slot in _overlaySlots)
                {
                    if (slot.Kind != OverlayElementKind.Quickslot)
                    {
                        continue;
                    }

                    slot.Preview = _captureSession.Crop(liveCapture, slot.Source.SourceRect);
                }

                _overlayWindow.RenderSlots(_overlaySlots);
            }
            RecordCpuRenderFrame(_activeRenderMode, _cpuRenderClock.ElapsedTicks);
        }
        catch (Exception ex)
        {
            _cpuStatsErrors++;
            _log.Error("Live overlay refresh failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus(L.F("Live overlay refresh failed: {0}", ex.Message));
        }
        finally
        {
            _isLiveRefreshInProgress = false;
        }
    }

    private bool HasLiveCaptureSource() =>
        _captureSession.HasLiveCaptureSource(CurrentCaptureBackend);

    private bool RegisterStopHotkey()
    {
        if (!HotkeyParser.TryParse(_stopHotkey, out var hotkey))
        {
            SetStatus("Invalid hotkey. Use a format like Ctrl+Shift+F8.");
            return false;
        }

        _hotkeyService ??= new HotkeyService();
        var registered = _hotkeyService.Register(new WindowInteropHelper(this).Handle, hotkey.Modifiers, hotkey.VirtualKey, () => StopOverlay());
        if (!registered)
        {
            _log.Info($"Stop hotkey registration failed: {hotkey.DisplayText}");
            SetStatus(L.F("Stop hotkey registration failed: {0}", hotkey.DisplayText));
        }
        else
        {
            _log.Info($"Stop hotkey registered: {hotkey.DisplayText}");
        }

        return registered;
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
