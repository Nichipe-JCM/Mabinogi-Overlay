using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow : Window
{
    private const int CandidateBorderPixels = 1;
    private const int CandidateVisualPaddingPixels = 1;
    private const int DebugDetectRuns = 100;
    private const double MinimumOverlaySlotSize = 1;
    private static readonly Color ProjectAccentColor = Color.FromRgb(0x89, 0xDE, 0xD4);
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly DxgiDesktopDuplicationCaptureService _dxgiCaptureService = new();
    private readonly WgcCaptureService _wgcCaptureService = new();
    private readonly RoiSectionDetectionService _roiSectionDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly CpuCompositedOverlayRenderer _cpuCompositedRenderer = new();
    private readonly MonitorTemplateDetectionService _monitorTemplateDetection = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ProfileStore _profileStore;
    private AppSettings _appSettings;
    private readonly AppLog _log = new();
    private readonly object _detectLogSync = new();
    private readonly string _detectSessionLogPath;
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _profileAutoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _internalTimerDebugTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<SlotCandidate> _candidates = new();
    private readonly ObservableCollection<QuickslotSection> _sections = new();
    private readonly List<OverlaySlot> _overlaySlots = new();
    private readonly List<InternalBuffTimer> _internalBuffTimers = new();
    private readonly HashSet<string> _recognizedBuffNameKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedBuffNameKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuffIconMatch> _buffIconMatches = new(StringComparer.Ordinal);
    private readonly List<Rectangle> _monitorDetectionRects = new();
    private readonly Dictionary<SlotCandidate, Rectangle> _candidateRects = new();
    private readonly SectionSettings[] _sectionSettings =
    [
        new(2, 5, 16),
        new(2, 5, 2)
    ];
    private readonly Stack<CandidateEditSnapshot> _undoStack = new();
    private readonly Stack<CandidateEditSnapshot> _redoStack = new();
    private IReadOnlyList<string> _profileNames = ["default"];
    private string _selectedProfileName = "default";
    private bool _isUpdatingProfileSelection;
    private bool _isLoadingProfile;
    private bool _isProfileDirty;
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
    private WgcSelectionResult? _wgcSelection;
    private OverlayWindow? _overlayWindow;
    private InternalTimerOverlayWindow? _internalTimerOverlayWindow;
    private ErinTimerWindow? _erinTimerWindow;
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
    private DebugDetectionExpectation _debugDetectionExpectation = DebugDetectionExpectation.TopGrouped1();
    private CandidateEditSnapshot? _candidateDragSnapshotBefore;
    private QuickslotSection? _selectedSection;
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
    private int _currentSectionIndex;
    private int _nextSectionId = 1;
    private double _layoutCanvasWidth = 360;
    private double _layoutCanvasHeight = 160;
    private double _overlayLeft = 120;
    private double _overlayTop = 120;
    private double _overlayOpacity = 1;
    private string _stopHotkey = "Ctrl+Shift+F8";
    private int _refreshFps = 30;
    private double _layoutSlotScale = 1.5;
    private double _layoutGridSnapSize = 10;
    private bool _buffMonitorEnabled;
    private bool _tuairimMonitorEnabled;
    private Rect? _buffMonitorRoi;
    private Rect? _tuairimMonitorRoi;
    private Rect? _tuairimAnchor;
    private string _lastStatusMessage = string.Empty;

    public MainWindow()
    {
        _appSettings = _settingsStore.Load();
        LocalizationService.Instance.SetLanguage(_appSettings.Language);
        _profileStore = new ProfileStore(_appSettings.ProfileDirectory);
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
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => FlushProfileAutoSave();
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= LocalizationService_LanguageChanged;
            _erinTimerWindow?.Close();
            StopOverlay(setStatus: false);
        };
        Deactivated += (_, _) => CancelInterruptedCaptureInteraction();
        CaptureCanvas.LostMouseCapture += (_, _) => CancelInterruptedCaptureInteraction();
        ApplySectionSettingsToControls(_currentSectionIndex);
        UpdateSizeLabels();
        CaptureZoomText.Text = "100%";
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
        UpdateInternalTimerDebugStatus();
        UpdateMonitorControlAvailability();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RefreshProfileList();
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
        UpdateInternalTimerDebugStatus();
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
            var window = FindAutoMabinogiWindow();
            if (window is null)
            {
                SetStatus("Auto capture failed: Mabinogi Client.exe window was not found.");
                _log.Info("Auto WGC capture failed: Mabinogi Client.exe window was not found.");
                return;
            }

            var selection = _wgcWindowSelection.CreateForWindow(window);
            if (selection is null)
            {
                SetStatus("Auto capture failed: WGC is not supported.");
                _log.Info("Auto WGC capture failed: WGC is not supported.");
                return;
            }

            _wgcSelection = selection;
            _selectedWindow = window;
            _capturedImage = await _wgcCaptureService.CaptureOnceAsync(selection.Item, TimeSpan.FromSeconds(3));
            ApplyCapturedPreview(_capturedImage, L.F("Auto captured WGC Mabinogi window: {0}", window.DisplayName));
            _log.Info($"Auto WGC capture succeeded: {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}, window={window.DisplayName}");
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
            var result = await _wgcWindowSelection.PickWindowAsync(this);
            if (result is null)
            {
                SetStatus("Manual capture canceled or WGC is not supported.");
                _log.Info("Manual WGC capture picker returned null.");
                return;
            }

            if (!result.LooksLikeMabinogi)
            {
                SetStatus(L.F("Manual capture rejected: selected window is not recognized as Mabinogi ({0}).", result.DisplayName));
                _log.Info($"Manual WGC capture rejected: {result.DisplayName}");
                return;
            }

            _wgcSelection = result;
            _selectedWindow = MatchPickedMabinogiWindow(result);
            _capturedImage = await _wgcCaptureService.CaptureOnceAsync(result.Item, TimeSpan.FromSeconds(3));
            ApplyCapturedPreview(_capturedImage, L.F("Manual captured WGC Mabinogi window: {0}", result.DisplayName));
            _log.Info($"Manual WGC capture succeeded: {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}, item={result.DisplayName}");
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
        _tuairimMonitorRoi = null;
        _tuairimAnchor = null;
        EnsureEnabledMonitorElementsPlaced();
        UpdateMonitorControlAvailability();
        SetStatus(L.F("{0}. Run slot detection next.", status));
    }

    private GameWindowInfo? FindAutoMabinogiWindow()
    {
        var windows = _windowDiscovery.GetVisibleWindows();
        var window = windows.FirstOrDefault(item => item.IsPreferredMabinogiClient)
                     ?? windows.FirstOrDefault(item => item.IsExactClientExecutable && item.LooksLikeMabinogi)
                     ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi);
        if (window is not null)
        {
            WindowCombo.ItemsSource = windows;
            WindowCombo.SelectedItem = window;
        }

        return window;
    }

    private GameWindowInfo? MatchPickedMabinogiWindow(WgcSelectionResult result)
    {
        var windows = _windowDiscovery.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        var window = windows.FirstOrDefault(item => item.IsPreferredMabinogiClient && MatchesWgcDisplayName(item, result.DisplayName))
                     ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi && MatchesWgcDisplayName(item, result.DisplayName))
                     ?? windows.FirstOrDefault(item => item.IsPreferredMabinogiClient)
                     ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi);
        if (window is not null)
        {
            WindowCombo.SelectedItem = window;
        }

        return window;
    }

    private static bool MatchesWgcDisplayName(GameWindowInfo window, string displayName) =>
        string.Equals(window.Title, displayName, StringComparison.OrdinalIgnoreCase)
        || displayName.Contains(window.Title, StringComparison.OrdinalIgnoreCase)
        || window.Title.Contains(displayName, StringComparison.OrdinalIgnoreCase);

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
                ? _captureService.Crop(_capturedImage!, candidate.SourceRect)
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
            slot.Preview = _captureService.Crop(_capturedImage, candidate.SourceRect);
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

            _candidates.Remove(candidate);
        }

        RemoveOverlaySlotsForCandidates(selected);
        RemoveSectionsContaining(selected);

        UpdateCandidateOverlayFlags();
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
            MessageBox.Show(
                this,
                L.T("Stop the overlay before opening Manage Layout."),
                L.T("Overlay is running"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

        editor.Applied += (_, _) =>
        {
            ApplyEditorState();
            ScheduleProfileAutoSave();
            FlushProfileAutoSave();
        };
        editor.ShowDialog();
        ApplyEditorState();
        ScheduleProfileAutoSave();
        SetStatus("Layout editor closed. Overlay settings updated.");
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

    private void ErinTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_erinTimerWindow is { IsLoaded: true })
        {
            if (_erinTimerWindow.WindowState == WindowState.Minimized)
            {
                _erinTimerWindow.WindowState = WindowState.Normal;
            }

            _erinTimerWindow.Activate();
            return;
        }

        _erinTimerWindow = new ErinTimerWindow(_log);
        _erinTimerWindow.Closed += (_, _) => _erinTimerWindow = null;
        _erinTimerWindow.Show();
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
            _buffMonitorRoi = null;
            _buffIconMatches.Clear();
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, _buffMonitorEnabled);
        UpdateMonitorControlAvailability();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
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
            RefreshMonitorDetectionVisuals();
        }
        SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, _tuairimMonitorEnabled);
        UpdateMonitorControlAvailability();
        RefreshInternalTimerOverlay();
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
        var settingsEditable = baseEditable && _monitorDetectionMode == MonitorDetectionMode.None;
        BuffMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        TuairimMonitorEnabledCheckBox.IsEnabled = settingsEditable;
        DetectBuffWindowButton.IsEnabled = baseEditable &&
                                           _buffMonitorEnabled &&
                                           _monitorDetectionMode is MonitorDetectionMode.None or MonitorDetectionMode.BuffWindow;
        LoadInternalTimerTestDataButton.IsEnabled = settingsEditable && _buffMonitorEnabled;
        DetectTuairimUiButton.IsEnabled = baseEditable &&
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

    private void LoadInternalTimerTestDataButton_Click(object sender, RoutedEventArgs e)
    {
        _internalBuffTimers.Clear();
        _internalBuffTimers.AddRange(
        [
            new InternalBuffTimer("monitor.buff.battle.overture", 120),
            new InternalBuffTimer("monitor.buff.march.song", 95),
            new InternalBuffTimer("monitor.buff.vivace", 70),
            new InternalBuffTimer("monitor.buff.harvest.song", 45)
        ]);
        _buffMonitorEnabled = true;
        BuffMonitorEnabledCheckBox.IsChecked = true;
        SetMonitorElementEnabled(OverlayElementKind.InternalBuffTimer, enabled: true);
        ApplyRecognizedBuffs(InternalBuffTimerPreviewRenderer.BuffNameKeys);
        UpdateInternalTimerDebugStatus();
        RefreshInternalTimerElementPreviews();
        RefreshInternalTimerOverlay();
        SetStatus("monitor.timer.debug.loaded");
    }

    private void InternalTimerDebugTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var timer in _internalBuffTimers)
        {
            timer.RemainingSeconds = Math.Max(0, timer.RemainingSeconds - 1);
        }

        UpdateInternalTimerDebugStatus();
        _internalTimerOverlayWindow?.SetTimers(_internalBuffTimers);
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
        if (timerSlot is null && tuairimSlot is null)
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
            _internalBuffTimers,
            _selectedBuffNameKeys)
        {
            Left = _overlayLeft,
            Top = _overlayTop
        };
        _internalTimerOverlayWindow.Show();
        _internalTimerOverlayWindow.UpdateLayout();
        if (_internalBuffTimers.Count > 0)
        {
            _internalTimerDebugTimer.Start();
        }
    }

    private void RefreshInternalTimerElementPreviews()
    {
        foreach (var slot in _overlaySlots.Where(slot => slot.Kind != OverlayElementKind.Quickslot))
        {
            slot.Preview = RenderMonitorElementPreview(slot.Kind);
        }
    }

    private void UpdateInternalTimerDebugStatus()
    {
        if (InternalTimerDebugStatusText is null)
        {
            return;
        }

        InternalTimerDebugStatusText.Text = _internalBuffTimers.Count == 0
            ? L.T("monitor.timer.debug.empty")
            : string.Join(
                " | ",
                _internalBuffTimers.Select(timer =>
                    $"{L.T(timer.NameKey)} {timer.RemainingSeconds / 60:00}:{timer.RemainingSeconds % 60:00}"));
    }

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
        return new OverlayProfile
        {
            Name = profileName,
            CanvasWidth = _layoutCanvasWidth,
            CanvasHeight = _layoutCanvasHeight,
            ScreenLeft = _overlayLeft,
            ScreenTop = _overlayTop,
            Opacity = _overlayOpacity,
            StopHotkey = _stopHotkey,
            RefreshIntervalMs = RefreshIntervalFromFps(_refreshFps),
            RefreshFps = _refreshFps,
            LayoutSlotScale = ReadLayoutSlotScale(),
            GridSnapSize = _layoutGridSnapSize,
            BuffMonitorEnabled = _buffMonitorEnabled,
            TuairimMonitorEnabled = _tuairimMonitorEnabled,
            RecognizedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
                .Where(_recognizedBuffNameKeys.Contains)
                .ToList(),
            SelectedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
                .Where(_selectedBuffNameKeys.Contains)
                .ToList(),
            BuffMonitorRoi = ToProfileRect(_buffMonitorRoi),
            BuffAnchors = _buffIconMatches.Values
                .OrderBy(match => match.Bounds.Y)
                .Select(match => new OverlayProfileBuffAnchor
                {
                    NameKey = match.NameKey,
                    Bounds = ToProfileRect(match.Bounds)!,
                    StructureScore = match.StructureScore,
                    IsActive = match.IsActive,
                    StateConfidence = match.StateConfidence
                })
                .ToList(),
            TuairimMonitorRoi = ToProfileRect(_tuairimMonitorRoi),
            TuairimAnchor = ToProfileRect(_tuairimAnchor),
            SlotInnerSize = Math.Min(ReadSlotInnerWidth(), ReadSlotInnerHeight()),
            SlotInnerWidth = ReadSlotInnerWidth(),
            SlotInnerHeight = ReadSlotInnerHeight(),
            SelectedSectionPattern = Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1),
            SectionSettings = _sectionSettings
                .Select((settings, index) => new OverlayProfileSectionSettings
                {
                    PatternIndex = index,
                    PatternName = GetSectionPatternName(index),
                    SmallGapX = settings.SmallGapX,
                    SmallGapY = settings.SmallGapY,
                    LargeGap = settings.LargeGap
                })
                .ToList(),
            Candidates = _candidates.Select(candidate => new OverlayProfileCandidate
            {
                Id = candidate.Id,
                SourceX = candidate.SourceRect.X,
                SourceY = candidate.SourceRect.Y,
                SourceWidth = candidate.SourceRect.Width,
                SourceHeight = candidate.SourceRect.Height,
                Score = candidate.Score,
                IsSelected = candidate.IsSelected,
                Kind = candidate.Kind,
                DisplayNameKey = candidate.DisplayNameKey,
                IsBuiltIn = candidate.IsBuiltIn
            }).ToList(),
            Sections = _sections.Select(section => new OverlayProfileSection
            {
                Id = section.Id,
                SeedCandidateId = section.Seed.Id,
                PatternIndex = section.PatternIndex,
                SmallGapX = section.Settings.SmallGapX,
                SmallGapY = section.Settings.SmallGapY,
                LargeGap = section.Settings.LargeGap,
                CandidateIds = section.Candidates.Select(candidate => candidate.Id).ToList()
            }).ToList(),
            Slots = _overlaySlots.Select(slot => new OverlayProfileSlot
            {
                SourceCandidateId = slot.Source.Id,
                SourceX = slot.Source.SourceRect.X,
                SourceY = slot.Source.SourceRect.Y,
                SourceWidth = slot.Source.SourceRect.Width,
                SourceHeight = slot.Source.SourceRect.Height,
                OverlayX = slot.OverlayRect.X,
                OverlayY = slot.OverlayRect.Y,
                OverlayWidth = slot.OverlayRect.Width,
                OverlayHeight = slot.OverlayRect.Height,
                Opacity = slot.Opacity,
                HasOpacityOverride = slot.HasOpacityOverride,
                Scale = slot.Scale
            }).ToList()
        };
    }

    private void ScheduleProfileAutoSave()
    {
        if (_isLoadingProfile || !IsLoaded)
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
        var profile = _profileStore.Load(profileName);
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
            _layoutCanvasWidth = Math.Max(120, profile.CanvasWidth);
        _layoutCanvasHeight = Math.Max(80, profile.CanvasHeight);
        _overlayLeft = profile.ScreenLeft;
        _overlayTop = profile.ScreenTop;
        _overlayOpacity = Math.Clamp(profile.Opacity, 0, 1);
        _stopHotkey = profile.StopHotkey;
        _refreshFps = CoerceRefreshFps(profile.RefreshFps > 0
            ? profile.RefreshFps
            : FpsFromInterval(profile.RefreshIntervalMs));
        _layoutSlotScale = Math.Clamp(profile.LayoutSlotScale, 0.1, 10);
        _layoutGridSnapSize = Math.Clamp(profile.GridSnapSize > 0 ? profile.GridSnapSize : 10, 1, 64);
        _buffMonitorEnabled = profile.BuffMonitorEnabled;
        _tuairimMonitorEnabled = profile.TuairimMonitorEnabled;
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _buffIconMatches.Clear();
        _buffMonitorRoi = FromProfileRect(profile.BuffMonitorRoi);
        _tuairimMonitorRoi = FromProfileRect(profile.TuairimMonitorRoi);
        _tuairimAnchor = FromProfileRect(profile.TuairimAnchor);
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
        var profileWidth = profile.SlotInnerWidth > 0 ? profile.SlotInnerWidth : profile.SlotInnerSize;
        var profileHeight = profile.SlotInnerHeight > 0 ? profile.SlotInnerHeight : profile.SlotInnerSize;
        SlotWidthBox.Text = ReadSlotDimensionText(profileWidth, ReadSlotInnerWidth());
        SlotHeightBox.Text = ReadSlotDimensionText(profileHeight, ReadSlotInnerHeight());
        ApplyProfileSectionSettings(profile);

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
                ? _captureService.Crop(_capturedImage!, candidate.SourceRect)
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
            _log.Info($"Profile loaded: {_profileStore.GetProfilePath(profileName)}, candidates={_candidates.Count}, slots={profile.Slots.Count}");
            SetStatus(L.F("Profile loaded: {0} ({1} candidates, {2} slots).", _profileStore.GetProfilePath(profileName), _candidates.Count, profile.Slots.Count));
        }
        finally
        {
            _profileAutoSaveTimer.Stop();
            _isProfileDirty = false;
            _isLoadingProfile = false;
        }
    }

    private void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
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
                _wgcCaptureService.StopLiveCapture();
                _liveOverlayTimer.Stop();

                SetStatus(L.F("Overlay click-through configuration failed: {0}", detail));
                return;
            }

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(_refreshFps));
            _activeRenderMode = _appSettings.OverlayRenderMode;
            var captureBackend = CurrentCaptureBackend;
            if (hasSlotOverlay && captureBackend != CaptureBackend.Wgc && _selectedWindow is null)
            {
                StopOverlay(setStatus: false);
                SetStatus(L.F("Run Auto capture or Manual capture before starting the overlay with {0}.", L.T(CaptureBackendLabel(captureBackend))));
                return;
            }

            ResetCpuRenderStats();
            var rendererMode = RenderModeLabel(_activeRenderMode);
            if (!hasSlotOverlay)
            {
                rendererMode = "monitor.internal.overlay";
            }
            else if (_activeRenderMode == OverlayRenderMode.GpuDxgi && captureBackend == CaptureBackend.Wgc && _wgcSelection is not null)
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
                    _wgcCaptureService.StartLiveCapture(_wgcSelection.Item);
                }
            }
            else if (_activeRenderMode == OverlayRenderMode.GpuDxgi)
            {
                _activeRenderMode = OverlayRenderMode.CpuWpf;
                rendererMode = $"{RenderModeLabel(OverlayRenderMode.CpuWpf)} fallback";
                _log.Info($"GPU/DXGI renderer requested with captureBackend={captureBackend}. Falling back to CPU/WPF renderer.");
            }
            else if (captureBackend == CaptureBackend.Wgc && _wgcSelection is not null)
            {
                _wgcCaptureService.StartLiveCapture(_wgcSelection.Item);
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
        var windows = _windowDiscovery.GetVisibleWindows();
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
            CaptureBackend.Wgc => _wgcSupport.IsSupported() ? "WGC supported" : "WGC unavailable",
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
        var clampedX = Math.Clamp(x, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Width - candidate.SourceRect.Width - CandidateBorderPixels));
        var clampedY = Math.Clamp(y, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Height - candidate.SourceRect.Height - CandidateBorderPixels));
        candidate.MoveTo(clampedX, clampedY);
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
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = ReferenceEquals(candidate, selected);
        }
    }

    private void ClearCandidateSelection()
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = false;
        }
    }

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
        var section = new QuickslotSection(_nextSectionId++, seed, patternIndex, settings, sectionCandidates);
        _sections.Add(section);
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
        var section = new QuickslotSection(_nextSectionId++, seed, patternIndex, settings, sectionCandidates);
        _sections.Add(section);
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
            return existing;
        }

        var candidate = new SlotCandidate(
            -1,
            new Rect(0, 0, InternalBuffTimerPreviewRenderer.BaseWidth, InternalBuffTimerPreviewRenderer.BaseHeight),
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
    }

    private bool IsMonitorElementEnabled(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => _buffMonitorEnabled,
        OverlayElementKind.TuairimGauge => _tuairimMonitorEnabled,
        _ => false
    };

    private void EnsureMonitorElementPlaced(OverlayElementKind kind, bool scheduleAutoSave = true)
    {
        var candidate = kind switch
        {
            OverlayElementKind.InternalBuffTimer => EnsureInternalTimerCandidate(),
            OverlayElementKind.TuairimGauge => EnsureTuairimGaugeCandidate(),
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
        OverlayElementKind.TuairimGauge => TuairimGaugePreviewRenderer.Render(),
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
    {
        var candidateSet = candidates.ToHashSet();
        var candidateIds = candidateSet.Select(candidate => candidate.Id).ToHashSet();
        return _overlaySlots.RemoveAll(slot =>
            candidateSet.Contains(slot.Source) || candidateIds.Contains(slot.Source.Id));
    }

    private void UpdateCandidateOverlayFlags()
    {
        var overlayCandidateIds = _overlaySlots.Select(slot => slot.Source.Id).ToHashSet();
        foreach (var candidate in _candidates)
        {
            candidate.IsInOverlay = overlayCandidateIds.Contains(candidate.Id);
        }
    }

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
        _sections.Clear();
        _selectedSection = null;
        _nextSectionId = 1;
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
    }

    private void RemoveSectionsContaining(IReadOnlyCollection<SlotCandidate> candidates)
    {
        var removed = _sections.Where(section => section.Candidates.Any(candidates.Contains)).ToList();
        foreach (var section in removed)
        {
            _sections.Remove(section);
        }

        if (_selectedSection is not null && removed.Contains(_selectedSection))
        {
            _selectedSection = null;
            SectionCombo.SelectedItem = null;
        }

        RefreshSectionLabels();
    }

    private void RefreshSectionLabels()
    {
        foreach (var candidate in _candidates)
        {
            candidate.SectionMembership = string.Empty;
        }

        foreach (var section in _sections)
        {
            foreach (var candidate in section.Candidates.Where(candidate => _candidates.Contains(candidate)))
            {
                candidate.SectionMembership = string.IsNullOrWhiteSpace(candidate.SectionMembership)
                    ? $"section {section.Id:00}"
                    : $"{candidate.SectionMembership},{section.Id:00}";
            }

            section.RefreshLabel();
        }

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
        var selectedSectionId = _selectedSection?.Id ?? 0;
        return new CandidateEditSnapshot(
            _candidates
                .Select(candidate => new CandidateState(
                    candidate.Id,
                    candidate.SourceRect.X,
                    candidate.SourceRect.Y,
                    candidate.SourceRect.Width,
                    candidate.SourceRect.Height,
                    candidate.Score,
                    candidate.IsSelected,
                    candidate.Kind,
                    candidate.DisplayNameKey,
                    candidate.IsBuiltIn))
                .ToList(),
            _sections
                .Select(section => new SectionState(
                    section.Id,
                    section.Seed.Id,
                    section.PatternIndex,
                    section.Settings.SmallGapX,
                    section.Settings.SmallGapY,
                    section.Settings.LargeGap,
                    section.Candidates.Select(candidate => candidate.Id).ToList()))
                .ToList(),
            selectedSectionId,
            _nextSectionId,
            selectedId);
    }

    private void PushUndoIfChanged(CandidateEditSnapshot before)
    {
        if (CandidateSnapshotsEqual(before, CaptureCandidateSnapshot()))
        {
            return;
        }

        _undoStack.Push(before);
        _redoStack.Clear();
        ScheduleProfileAutoSave();
    }

    private void UndoCandidateEdit()
    {
        if (_undoStack.Count == 0)
        {
            SetStatus("No candidate edit to undo.");
            return;
        }

        var current = CaptureCandidateSnapshot();
        var previous = _undoStack.Pop();
        _redoStack.Push(current);
        RestoreCandidateSnapshot(previous);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit undone.");
    }

    private void RedoCandidateEdit()
    {
        if (_redoStack.Count == 0)
        {
            SetStatus("No candidate edit to redo.");
            return;
        }

        var current = CaptureCandidateSnapshot();
        var next = _redoStack.Pop();
        _undoStack.Push(current);
        RestoreCandidateSnapshot(next);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit redone.");
    }

    private void RestoreCandidateSnapshot(CandidateEditSnapshot snapshot)
    {
        _candidates.Clear();
        ClearCandidateRects();
        _sections.Clear();
        _selectedSection = null;

        SlotCandidate? selected = null;
        var restoredById = new Dictionary<int, SlotCandidate>();
        foreach (var saved in snapshot.Candidates)
        {
            if (saved.Kind != OverlayElementKind.Quickslot && !IsMonitorElementEnabled(saved.Kind))
            {
                continue;
            }

            var candidate = new SlotCandidate(
                saved.Id,
                new Rect(saved.X, saved.Y, saved.Width, saved.Height),
                saved.Score,
                saved.Kind,
                saved.DisplayNameKey,
                saved.IsBuiltIn)
            {
                IsSelected = saved.IsSelected
            };
            AddCandidate(candidate);
            restoredById[candidate.Id] = candidate;
            if (saved.Id == snapshot.SelectedId)
            {
                selected = candidate;
            }
        }

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

        CandidateList.SelectedItem = selected;
        QuickslotSection? selectedSection = null;
        foreach (var savedSection in snapshot.Sections)
        {
            if (!restoredById.TryGetValue(savedSection.SeedId, out var seed))
            {
                continue;
            }

            var candidates = savedSection.CandidateIds
                .Select(id => restoredById.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate is not null)
                .Cast<SlotCandidate>()
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var section = new QuickslotSection(
                savedSection.Id,
                seed,
                savedSection.PatternIndex,
                new SectionSettings(savedSection.SmallGapX, savedSection.SmallGapY, savedSection.LargeGap),
                candidates);
            _sections.Add(section);
            if (savedSection.Id == snapshot.SelectedSectionId)
            {
                selectedSection = section;
            }
        }

        _nextSectionId = Math.Max(snapshot.NextSectionId, _sections.Count == 0 ? 1 : _sections.Max(section => section.Id) + 1);
        if (selectedSection is not null)
        {
            SelectSection(selectedSection);
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
                slot.Preview = _captureService.Crop(_capturedImage, restoredSource.SourceRect);
            }
        }

        UpdateLayoutSummary();
    }

    private static bool CandidateSnapshotsEqual(CandidateEditSnapshot left, CandidateEditSnapshot right)
    {
        if (left.SelectedId != right.SelectedId ||
            left.SelectedSectionId != right.SelectedSectionId ||
            left.NextSectionId != right.NextSectionId ||
            left.Candidates.Count != right.Candidates.Count ||
            left.Sections.Count != right.Sections.Count)
        {
            return false;
        }

        return left.Candidates.SequenceEqual(right.Candidates) &&
               left.Sections.Zip(right.Sections).All(pair => SectionStatesEqual(pair.First, pair.Second));
    }

    private static bool SectionStatesEqual(SectionState left, SectionState right) =>
        left.Id == right.Id &&
        left.SeedId == right.SeedId &&
        left.PatternIndex == right.PatternIndex &&
        left.SmallGapX.Equals(right.SmallGapX) &&
        left.SmallGapY.Equals(right.SmallGapY) &&
        left.LargeGap.Equals(right.LargeGap) &&
        left.CandidateIds.SequenceEqual(right.CandidateIds);

    private int NextCandidateId()
    {
        var highest = _candidates.Where(candidate => candidate.Id > 0).Select(candidate => candidate.Id).DefaultIfEmpty(0).Max();
        return highest + 1;
    }

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
        _internalTimerDebugTimer.Stop();
        LogCpuRenderStats(final: true);
        _gpuLiveOverlayService?.Dispose();
        _gpuLiveOverlayService = null;
        _wgcCaptureService.StopLiveCapture();
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
                if (_wgcCaptureService.LastLiveCaptureException is not null)
                {
                    throw new InvalidOperationException("Live WGC capture failed.", _wgcCaptureService.LastLiveCaptureException);
                }

                if (!_wgcCaptureService.TryGetLatestFrame(out var latestFrame) || latestFrame is null)
                {
                    return;
                }

                liveCapture = latestFrame;
            }
            else if (captureBackend == CaptureBackend.DxgiDesktopDuplication)
            {
                liveCapture = _dxgiCaptureService.CaptureClientArea(_selectedWindow!);
            }
            else
            {
                liveCapture = _captureService.CaptureClientArea(_selectedWindow!);
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

                    slot.Preview = _captureService.Crop(liveCapture, slot.Source.SourceRect);
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
        CurrentCaptureBackend == CaptureBackend.Wgc
            ? _wgcSelection is not null
            : _selectedWindow is not null;

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

    private void ApplyProfileSectionSettings(OverlayProfile profile)
    {
        foreach (var saved in profile.SectionSettings)
        {
            if (saved.PatternIndex < 0 || saved.PatternIndex >= _sectionSettings.Length)
            {
                continue;
            }

            _sectionSettings[saved.PatternIndex] = new SectionSettings(
                Math.Clamp(saved.SmallGapX, 2, 30),
                Math.Clamp(saved.SmallGapY, 2, 30),
                Math.Clamp(saved.LargeGap, 2, 60));
        }

        _currentSectionIndex = Math.Clamp(profile.SelectedSectionPattern, 0, _sectionSettings.Length - 1);
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
        var names = _profileStore.ListProfileNames().ToList();
        if (names.Count == 0)
        {
            names.Add("default");
        }

        var selected = string.IsNullOrWhiteSpace(selectedProfileName)
            ? ReadSelectedProfileName()
            : selectedProfileName.Trim();
        if (!names.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(selected);
            names = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        _profileNames = names;
        _selectedProfileName = names.FirstOrDefault(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
                               ?? names.FirstOrDefault()
                               ?? "default";
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

    private static string GetSectionPatternName(int index) =>
        index == 1 ? SectionPattern.Vertical().Name : SectionPattern.TopGrouped().Name;

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

    private sealed class QuickslotSection
    {
        public QuickslotSection(int id, SlotCandidate seed, int patternIndex, SectionSettings settings, List<SlotCandidate> candidates)
        {
            Id = id;
            Seed = seed;
            PatternIndex = patternIndex;
            Settings = settings;
            Candidates = candidates;
        }

        public int Id { get; }

        public SlotCandidate Seed { get; }

        public int PatternIndex { get; }

        public SectionSettings Settings { get; set; }

        public List<SlotCandidate> Candidates { get; }

        public string Label => $"#{Id:00} {GetSectionPatternName(PatternIndex)} ({Candidates.Count})";

        public void RefreshLabel()
        {
        }
    }

    private sealed record SectionSettings(double SmallGapX, double SmallGapY, double LargeGap);

    private sealed record CandidateEditSnapshot(
        List<CandidateState> Candidates,
        List<SectionState> Sections,
        int SelectedSectionId,
        int NextSectionId,
        int SelectedId);

    private sealed record CandidateState(
        int Id,
        double X,
        double Y,
        double Width,
        double Height,
        double Score,
        bool IsSelected,
        OverlayElementKind Kind,
        string? DisplayNameKey,
        bool IsBuiltIn);

    private sealed record SectionState(
        int Id,
        int SeedId,
        int PatternIndex,
        double SmallGapX,
        double SmallGapY,
        double LargeGap,
        List<int> CandidateIds);

    private sealed record SectionPattern(
        string Name,
        int GroupColumns,
        int GroupRows,
        int GroupColumnsCount,
        int GroupRowsCount,
        Func<double, double> InnerGapX,
        Func<double, double> InnerGapY,
        Func<double, double> GroupGapX,
        Func<double, double> GroupGapY)
    {
        public static SectionPattern TopGrouped() => new(
            "top grouped 4x2 x3",
            4,
            2,
            3,
            1,
            smallGap => smallGap,
            smallGap => smallGap,
            largeGap => largeGap,
            _ => 0);

        public static SectionPattern Vertical() => new(
            "vertical 2x8",
            2,
            8,
            1,
            1,
            smallGap => smallGap,
            smallGap => smallGap,
            _ => 0,
            _ => 0);
    }
}
