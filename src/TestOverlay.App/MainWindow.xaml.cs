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
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly DxgiDesktopDuplicationCaptureService _dxgiCaptureService = new();
    private readonly WgcCaptureService _wgcCaptureService = new();
    private readonly RoiSectionDetectionService _roiSectionDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly CpuCompositedOverlayRenderer _cpuCompositedRenderer = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ProfileStore _profileStore;
    private AppSettings _appSettings;
    private readonly AppLog _log = new();
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<SlotCandidate> _candidates = new();
    private readonly ObservableCollection<QuickslotSection> _sections = new();
    private readonly List<OverlaySlot> _overlaySlots = new();
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
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
    private GameWindowInfo? _verifiedGdiWindow;
    private GameWindowInfo? _verifiedDxgiWindow;
    private WgcSelectionResult? _wgcSelection;
    private OverlayWindow? _overlayWindow;
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

    public MainWindow()
    {
        _appSettings = _settingsStore.Load();
        _profileStore = new ProfileStore(_appSettings.ProfileDirectory);
        InitializeComponent();
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
        };
        SmallGapXSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        SmallGapYSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        LargeGapSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => StopOverlay(setStatus: false);
        Deactivated += (_, _) => CancelInterruptedCaptureInteraction();
        CaptureCanvas.LostMouseCapture += (_, _) => CancelInterruptedCaptureInteraction();
        ApplySectionSettingsToControls(_currentSectionIndex);
        InitializeCaptureBackendOptions();
        UpdateSizeLabels();
        CaptureZoomText.Text = "100%";
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RefreshProfileList();
        _log.Info("Application loaded.");
    }

    private void InitializeCaptureBackendOptions()
    {
        var options = new List<CaptureBackendOption>
        {
            new(CaptureBackend.Wgc, "WGC window"),
            new(CaptureBackend.DxgiDesktopDuplication, "DXGI monitor"),
            new(CaptureBackend.GdiBitBlt, "GDI BitBlt")
        };
        CaptureBackendCombo.ItemsSource = options;
        CaptureBackendCombo.SelectedItem = options.FirstOrDefault(option => option.Backend == _appSettings.CaptureBackend) ?? options[0];
    }

    private CaptureBackend CurrentCaptureBackend =>
        CaptureBackendCombo?.SelectedItem is CaptureBackendOption option
            ? option.Backend
            : _appSettings.CaptureBackend;

    private string CurrentCaptureBackendLabel =>
        CaptureBackendCombo?.SelectedItem is CaptureBackendOption option
            ? option.Label
            : CaptureBackendLabel(_appSettings.CaptureBackend);

    private void CaptureBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureBackendCombo.SelectedItem is not CaptureBackendOption option)
        {
            return;
        }

        _appSettings.CaptureBackend = option.Backend;
        _settingsStore.Save(_appSettings);
        WindowStatusText.Text = BuildWindowStatusText();
        SetStatus($"Capture method selected: {option.Label}. Verify before capture.");
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowStatusText is not null)
        {
            WindowStatusText.Text = BuildWindowStatusText();
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

    private void SlotSizeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSizeLabels();

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

    private async void VerifyWgcButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = CurrentCaptureBackend;
        try
        {
            switch (backend)
            {
                case CaptureBackend.Wgc:
                    var result = await _wgcWindowSelection.PickWindowAsync(this);
                    if (result is null)
                    {
                        SetStatus("WGC window selection was canceled or WGC is not supported.");
                        _log.Info("WGC window selection returned null.");
                        return;
                    }

                    _wgcSelection = result;
                    _log.Info($"WGC selection: {result.DisplayName}, {result.Width}x{result.Height}, looksLikeMabinogi={result.LooksLikeMabinogi}");
                    SetStatus(result.LooksLikeMabinogi
                        ? $"WGC verified Mabinogi window: {result.DisplayName} ({result.Width}x{result.Height})"
                        : $"WGC selected window is not recognized as Mabinogi: {result.DisplayName} ({result.Width}x{result.Height})");
                    break;
                case CaptureBackend.DxgiDesktopDuplication:
                    var dxgiWindow = RequireSelectedWindow();
                    var dxgiResult = _dxgiCaptureService.Verify(dxgiWindow);
                    _verifiedDxgiWindow = dxgiWindow;
                    _selectedWindow = dxgiWindow;
                    _log.Info(
                        $"DXGI verification: window={dxgiWindow.DisplayName}, output={dxgiResult.OutputName}, " +
                        $"client={dxgiResult.ClientWidth}x{dxgiResult.ClientHeight}@{dxgiResult.ClientScreenX},{dxgiResult.ClientScreenY}, " +
                        $"outputRect={dxgiResult.OutputWidth}x{dxgiResult.OutputHeight}@{dxgiResult.OutputLeft},{dxgiResult.OutputTop}, " +
                        $"looksLikeMabinogi={dxgiWindow.LooksLikeMabinogi}");
                    SetStatus(dxgiWindow.LooksLikeMabinogi
                        ? $"DXGI verified Mabinogi window on {dxgiResult.OutputName}: {dxgiResult.ClientWidth}x{dxgiResult.ClientHeight}"
                        : $"DXGI verified selected window, but it is not recognized as Mabinogi: {dxgiWindow.DisplayName}");
                    break;
                case CaptureBackend.GdiBitBlt:
                    var gdiWindow = RequireSelectedWindow();
                    _ = _captureService.CaptureClientArea(gdiWindow);
                    _verifiedGdiWindow = gdiWindow;
                    _selectedWindow = gdiWindow;
                    _log.Info($"GDI verification: window={gdiWindow.DisplayName}, looksLikeMabinogi={gdiWindow.LooksLikeMabinogi}");
                    SetStatus(gdiWindow.LooksLikeMabinogi
                        ? $"GDI verified Mabinogi window: {gdiWindow.DisplayName}"
                        : $"GDI verified selected window, but it is not recognized as Mabinogi: {gdiWindow.DisplayName}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"{CaptureBackendLabel(backend)} verification failed.", ex);
            SetStatus($"{CaptureBackendLabel(backend)} verification failed: {ex.Message}");
        }
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = CurrentCaptureBackend;
        try
        {
            CaptureButton.IsEnabled = false;
            _capturedImage = backend switch
            {
                CaptureBackend.Wgc => await CaptureWgcOnceAsync(),
                CaptureBackend.DxgiDesktopDuplication => CaptureDxgiOnce(),
                CaptureBackend.GdiBitBlt => CaptureGdiOnce(),
                _ => throw new InvalidOperationException("Unsupported capture backend.")
            };
            _log.Info($"{CaptureBackendLabel(backend)} capture succeeded: {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}");
            CaptureImage.Source = _capturedImage;
            CaptureCanvas.Width = _capturedImage.PixelWidth;
            CaptureCanvas.Height = _capturedImage.PixelHeight;
            CaptureInfoText.Text = $"{_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}";
            _candidates.Clear();
            ClearCandidateRects();
            ClearSections();
            SetStatus($"Captured with {CaptureBackendLabel(backend)}. Run slot detection next.");
        }
        catch (Exception ex)
        {
            _log.Error($"{CaptureBackendLabel(backend)} capture failed.", ex);
            SetStatus($"{CaptureBackendLabel(backend)} capture failed: {ex.Message}");
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    private async Task<BitmapSource> CaptureWgcOnceAsync()
    {
        if (_wgcSelection is null)
        {
            throw new InvalidOperationException("Verify a WGC window before capture.");
        }

        if (!_wgcSelection.LooksLikeMabinogi)
        {
            throw new InvalidOperationException("WGC verification selected a non-Mabinogi window. Verify WGC again.");
        }

        _selectedWindow = WindowCombo.SelectedItem as GameWindowInfo;
        return await _wgcCaptureService.CaptureOnceAsync(_wgcSelection.Item, TimeSpan.FromSeconds(3));
    }

    private BitmapSource CaptureDxgiOnce()
    {
        var window = _verifiedDxgiWindow ?? throw new InvalidOperationException("Verify a DXGI window before capture.");
        if (!window.LooksLikeMabinogi)
        {
            throw new InvalidOperationException("DXGI verification selected a non-Mabinogi window. Verify DXGI again.");
        }

        _selectedWindow = window;
        return _dxgiCaptureService.CaptureClientArea(window);
    }

    private BitmapSource CaptureGdiOnce()
    {
        var window = _verifiedGdiWindow ?? throw new InvalidOperationException("Verify a GDI window before capture.");
        if (!window.LooksLikeMabinogi)
        {
            throw new InvalidOperationException("GDI verification selected a non-Mabinogi window. Verify GDI again.");
        }

        _selectedWindow = window;
        return _captureService.CaptureClientArea(window);
    }

    private GameWindowInfo RequireSelectedWindow() =>
        WindowCombo.SelectedItem as GameWindowInfo
        ?? throw new InvalidOperationException("Select a window before verification.");

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
        SetStatus($"Debug detect: drag one {expectation.Label} ROI. It will run 100 simulations and save a log.");
    }

    private void AddToOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlaceSelectedCandidates(clearExisting: false);
    }

    private void PlaceSelectedCandidates(bool clearExisting)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture and detect candidates first.");
            return;
        }

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No checked candidates. Check slots to place first.");
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
            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var width = Math.Max(16, candidate.SourceRect.Width * ReadLayoutSlotScale());
            var height = Math.Max(16, candidate.SourceRect.Height * ReadLayoutSlotScale());
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
        _log.Info($"Placed overlay slots: added={selected.Count}, total={_overlaySlots.Count}, clearExisting={clearExisting}, canvas={_layoutCanvasWidth}x{_layoutCanvasHeight}");
        SetStatus(clearExisting
            ? $"Placed {_overlaySlots.Count} slots. Open Manage Layout to arrange them."
            : $"Added {selected.Count} slot(s) to overlay. Total {_overlaySlots.Count}.");
    }

    private void SelectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = true;
        }

        SetStatus($"Selected all {_candidates.Count} candidates.");
    }

    private void DeselectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = false;
        }

        SetStatus($"Deselected all {_candidates.Count} candidates.");
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

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate highlighted)
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
            var overlayWidth = Math.Max(16, slot.OverlayRect.Width * width / oldWidth);
            var overlayHeight = Math.Max(16, slot.OverlayRect.Height * height / oldHeight);
            slot.OverlayRect = new Rect(slot.OverlayRect.X, slot.OverlayRect.Y, overlayWidth, overlayHeight);
        }

        PushUndoIfChanged(before);
        SetStatus($"Applied candidate size {width}x{height} to #{candidate.Id:000}.");
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
        SetStatus($"Generated {pattern.Name} from #{seed.Id:000}. Adjust small/large gap sliders to align the section.");
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

        _overlaySlots.RemoveAll(slot => candidates.Contains(slot.Source));
        _sections.Remove(_selectedSection);
        _selectedSection = null;
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus($"Deleted section and {candidates.Count} candidate(s).");
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
        SetStatus($"Selected section {_selectedSection.Label}.");
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

        _overlaySlots.RemoveAll(slot => selected.Contains(slot.Source));
        RemoveSectionsContaining(selected);

        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus($"Deleted {selected.Count} selected candidates.");
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
        var before = CaptureCandidateSnapshot();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();
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
                "Stop the overlay before opening Manage Layout.",
                "Overlay is running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Stop the overlay before opening Manage Layout.");
            return;
        }

        if (_overlaySlots.Count == 0)
        {
            SetStatus("No overlay slots are placed.");
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
        editor.ShowDialog();
        _layoutCanvasWidth = editor.CanvasWidth;
        _layoutCanvasHeight = editor.CanvasHeight;
        _overlayLeft = editor.ScreenLeft;
        _overlayTop = editor.ScreenTop;
        _overlayOpacity = editor.OverlayOpacity;
        _stopHotkey = editor.StopHotkey;
        _refreshFps = editor.RefreshFps;
        _layoutSlotScale = editor.SlotScale;
        _layoutGridSnapSize = editor.GridSnapSize;
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        SetStatus("Layout editor closed. Overlay settings updated.");
    }

    private void ClearLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        ClearLayout();
        SetStatus("Overlay layout cleared.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _profileStore.ProfileDirectory,
            _settingsStore.DefaultProfileDirectory,
            _profileNames,
            ReadSelectedProfileName(),
            _appSettings.OverlayRenderMode)
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
            var directory = _settingsStore.NormalizeProfileDirectory(dialog.ProfileDirectory);
            System.IO.Directory.CreateDirectory(directory);
            _appSettings.ProfileDirectory = directory;
            _appSettings.OverlayRenderMode = dialog.SelectedRenderMode;
            _settingsStore.Save(_appSettings);
            _profileStore.SetProfileDirectory(directory);
            RefreshProfileList(dialog.SelectedProfileName);
            _log.Info($"Settings saved: profileDirectory={directory}, renderMode={_appSettings.OverlayRenderMode}");
        }
        catch (Exception exception)
        {
            _log.Error("Failed to save settings.", exception);
            SetStatus($"Settings save failed: {exception.Message}");
            return;
        }

        switch (dialog.RequestedProfileAction)
        {
            case SettingsProfileAction.Save:
                SaveCurrentProfile();
                break;
            case SettingsProfileAction.Load:
                LoadSelectedProfile();
                break;
            default:
                SetStatus($"Settings saved: {_profileStore.ProfileDirectory}, renderer={RenderModeLabel(_appSettings.OverlayRenderMode)}");
                break;
        }
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e) => SaveCurrentProfile();

    private void SaveCurrentProfile()
    {
        SaveCurrentSectionSettings();
        var dialog = new ProfileNameDialog(ReadSelectedProfileName())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            SetStatus("Profile save canceled.");
            return;
        }

        var profileName = dialog.ProfileName;
        var profile = new OverlayProfile
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
                IsSelected = candidate.IsSelected
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
                Scale = slot.Scale
            }).ToList()
        };

        var path = _profileStore.Save(profile, profileName);
        RefreshProfileList(System.IO.Path.GetFileNameWithoutExtension(path));
        _log.Info($"Profile saved: {path}, candidates={profile.Candidates.Count}, slots={profile.Slots.Count}");
        SetStatus($"Profile saved: {path}");
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e) => LoadSelectedProfile();

    private void LoadSelectedProfile()
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before loading a profile.");
            return;
        }

        var profileName = ReadSelectedProfileName();
        var profile = _profileStore.Load(profileName);
        if (profile is null)
        {
            SetStatus($"No saved profile exists: {_profileStore.GetProfilePath(profileName)}");
            return;
        }

        RefreshProfileList(profileName);
        _layoutCanvasWidth = Math.Max(120, profile.CanvasWidth);
        _layoutCanvasHeight = Math.Max(80, profile.CanvasHeight);
        _overlayLeft = profile.ScreenLeft;
        _overlayTop = profile.ScreenTop;
        _overlayOpacity = Math.Clamp(profile.Opacity, 0.2, 1);
        _stopHotkey = profile.StopHotkey;
        _refreshFps = CoerceRefreshFps(profile.RefreshFps > 0
            ? profile.RefreshFps
            : FpsFromInterval(profile.RefreshIntervalMs));
        _layoutSlotScale = Math.Clamp(profile.LayoutSlotScale, 1, 3);
        _layoutGridSnapSize = Math.Clamp(profile.GridSnapSize > 0 ? profile.GridSnapSize : 10, 1, 64);
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
                var candidate = new SlotCandidate(
                    savedCandidate.Id,
                    new Rect(
                        savedCandidate.SourceX,
                        savedCandidate.SourceY,
                        savedCandidate.SourceWidth,
                        savedCandidate.SourceHeight),
                    savedCandidate.Score)
                {
                    IsSelected = savedCandidate.IsSelected
                };
                AddCandidate(candidate);
                loadedCandidates[candidate.Id] = candidate;
            }
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

        var nextCandidateId = loadedCandidates.Count == 0 ? 1 : loadedCandidates.Keys.Max() + 1;
        foreach (var savedSlot in profile.Slots)
        {
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

            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop,
                savedSlot.Opacity > 0 ? savedSlot.Opacity : 1,
                savedSlot.Scale > 0 ? savedSlot.Scale : InferSlotScale(savedSlot));
            _overlaySlots.Add(slot);
        }

        _nextSectionId = _sections.Count == 0 ? 1 : _sections.Max(section => section.Id) + 1;
        RefreshSectionLabels();
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        _log.Info($"Profile loaded: {_profileStore.GetProfilePath(profileName)}, candidates={_candidates.Count}, slots={profile.Slots.Count}");
        SetStatus($"Profile loaded: {_profileStore.GetProfilePath(profileName)} ({_candidates.Count} candidates, {profile.Slots.Count} slots).");
    }

    private void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
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

                SetStatus($"Overlay click-through configuration failed: {detail}");
                return;
            }

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(_refreshFps));
            _activeRenderMode = _appSettings.OverlayRenderMode;
            var captureBackend = CurrentCaptureBackend;
            if (captureBackend != CaptureBackend.Wgc && _selectedWindow is null)
            {
                StopOverlay(setStatus: false);
                SetStatus($"Verify and capture a window with {CaptureBackendLabel(captureBackend)} before starting the overlay.");
                return;
            }

            ResetCpuRenderStats();
            var rendererMode = RenderModeLabel(_activeRenderMode);
            if (_activeRenderMode == OverlayRenderMode.GpuDxgi && captureBackend == CaptureBackend.Wgc && _wgcSelection is not null)
            {
                try
                {
                    _gpuLiveOverlayService = new GpuLiveOverlayService(
                        new WindowInteropHelper(_overlayWindow).Handle,
                        (int)Math.Ceiling(_layoutCanvasWidth),
                        (int)Math.Ceiling(_layoutCanvasHeight),
                        _wgcSelection.Item,
                        _overlaySlots,
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

            _liveOverlayTimer.Start();
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
            SetStatus($"Overlay started ({clickThroughStatus}, {CaptureBackendLabel(captureBackend)}, {rendererMode}). Stop hotkey: {_stopHotkey}");
        }
        catch (Exception ex)
        {
            _log.Error("Overlay start failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus($"Overlay start failed: {ex.Message}");
        }
    }

    private void StopOverlayButton_Click(object sender, RoutedEventArgs e) => StopOverlay();

    private void CaptureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CaptureCanvas);
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

    private void RefreshWindows()
    {
        var windows = _windowDiscovery.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.LooksLikeMabinogi) ?? windows.FirstOrDefault();
        WindowStatusText.Text = BuildWindowStatusText();
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
                ? $"Mabinogi window recognized. ({captureStatus})"
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

        var hadSelection = _selectionRect is not null ||
                           _isSelectingDetectionRoi ||
                           _isSelectingDebugDetectionRoi ||
                           _isSelectingCandidates;
        var hadDrag = _draggingCandidate is not null;
        if (!hadSelection && !hadDrag)
        {
            return;
        }

        RemoveSelectionRectangle();
        if (_isSelectingDetectionRoi)
        {
            _isSelectingDetectionRoi = false;
            SetDetectionMode(active: false);
        }

        if (_isSelectingDebugDetectionRoi)
        {
            _isSelectingDebugDetectionRoi = false;
            SetDebugDetectionMode(active: false);
        }

        _isSelectingCandidates = false;
        if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var draggingRect))
        {
            ReleaseCaptureSafely(draggingRect);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
        ReleaseCaptureSafely(CaptureCanvas);
        SetStatus("Interrupted preview drag was canceled.");
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
            Stroke = Brushes.Gold,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 215, 0)),
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
            Stroke = Brushes.Magenta,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(26, 255, 0, 255)),
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

        var patternKind = ChooseDetectionPatternKind();
        if (patternKind is null)
        {
            SetStatus("Section detection canceled.");
            return;
        }

        DetectSectionInRoi(roi, patternKind.Value);
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

    private void SetDetectionMode(bool active)
    {
        if (active)
        {
            SetDebugDetectionMode(active: false);
        }

        _isAwaitingDetectionRoi = active;
        if (DetectModeText is not null)
        {
            DetectModeText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DetectButton is not null)
        {
            DetectButton.Content = active ? "Drag ROI..." : "Auto detect section";
            DetectButton.Background = active ? Brushes.Gold : SystemColors.ControlBrush;
        }
    }

    private void SetDebugDetectionMode(bool active)
    {
        if (active)
        {
            SetDetectionMode(active: false);
        }

        _isAwaitingDebugDetectionRoi = active;
        if (DebugDetectButton is not null)
        {
            DebugDetectButton.Content = active ? "Drag debug ROI..." : "Debug detect";
            DebugDetectButton.Background = active ? Brushes.Magenta : SystemColors.ControlBrush;
        }
    }

    private QuickslotSectionPatternKind? ChooseDetectionPatternKind()
    {
        var result = MessageBox.Show(
            this,
            "Choose the quickslot section type.\n\nYes: Top grouped 4x2 x3\nNo: Vertical 2x8\nCancel: cancel detection",
            "Section Type",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => QuickslotSectionPatternKind.TopGrouped,
            MessageBoxResult.No => QuickslotSectionPatternKind.Vertical,
            _ => null
        };
    }

    private DebugDetectionExpectation? ChooseDebugDetectionExpectation()
    {
        var sectionResult = MessageBox.Show(
            this,
            "Choose the debug detect target.\n\nYes: Top grouped 4x2 x3\nNo: Vertical 2x8\nCancel: cancel debug detection",
            "Debug Detect Target",
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
            "Choose the top grouped debug target.\n\nYes: Top grouped 1 (x=7, y=18)\nNo: Top grouped 2 (x=627, y=18)\nCancel: cancel debug detection",
            "Top Grouped Debug Target",
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
        var result = _roiSectionDetection.Detect(
            _capturedImage,
            roi,
            patternKind);

        if (result is null)
        {
            SetStatus("No matching quickslot section pattern was found in the selected area.");
            return;
        }

        AddDetectedSection(result);
        PushUndoIfChanged(before);

        _log.Info(
            $"ROI section detection completed: pattern={patternKind}, roi={roi.X:0},{roi.Y:0},{roi.Width:0}x{roi.Height:0}, " +
            $"slots={result.Slots.Count}, gapX={result.SmallGapX:0}, gapY={result.SmallGapY:0}, largeGap={result.LargeGap:0}, score={result.Score:0.00}");
        SetStatus(
            $"Added {GetSectionPatternName(PatternIndexFromKind(patternKind))}: " +
            $"{result.Slots.Count} slots, slot {result.Slots[0].Width:0}x{result.Slots[0].Height:0}px, " +
            $"gap X {result.SmallGapX:0}px, gap Y {result.SmallGapY:0}px, large gap {result.LargeGap:0}px.");
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
        SetStatus($"Debug detect finished: exact {exactMatches}/{DebugDetectRuns}, detected {detections}/{DebugDetectRuns}. Log: {logPath}");
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
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(35, 0, 191, 255)),
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
        SetStatus($"Adjusted {_selectedSection.Label}: gap X {ReadSmallGapX():0}px, gap Y {ReadSmallGapY():0}px, large gap {ReadLargeGap():0}px.");
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
        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate highlighted)
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

        SetStatus($"Nudged {selected.Count} candidate(s) by 1px.");
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
            }
        };
        AddCandidateVisual(candidate);
    }

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
        UpdateLayoutSummary();
        UpdateCandidateOverlayFlags();
    }

    private void UpdateCandidateOverlayFlags()
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsInOverlay = _overlaySlots.Any(slot => ReferenceEquals(slot.Source, candidate));
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
                    candidate.IsSelected))
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
            var candidate = new SlotCandidate(
                saved.Id,
                new Rect(saved.X, saved.Y, saved.Width, saved.Height),
                saved.Score)
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
            if (_capturedImage is not null)
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

    private int NextCandidateId() => _candidates.Count == 0 ? 1 : _candidates.Max(candidate => candidate.Id) + 1;

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
        LogCpuRenderStats(final: true);
        _gpuLiveOverlayService?.Dispose();
        _gpuLiveOverlayService = null;
        _wgcCaptureService.StopLiveCapture();
        _overlayWindow?.Close();
        _overlayWindow = null;
        _hotkeyService?.Dispose();
        _hotkeyService = null;
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
                    (int)Math.Ceiling(_layoutCanvasHeight));
                _overlayWindow.RenderCompositedFrame(compositedFrame);
            }
            else
            {
                foreach (var slot in _overlaySlots)
                {
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
            SetStatus($"Live overlay refresh failed: {ex.Message}");
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
            SetStatus($"Stop hotkey registration failed: {hotkey.DisplayText}");
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

    private double ReadLayoutSlotScale() => Math.Clamp(_layoutSlotScale, 1, 3);

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
            SlotSizeText.Text = $"inside {ReadSlotInnerWidth()}x{ReadSlotInnerHeight()}px, box {ReadCandidateBoxWidth()}x{ReadCandidateBoxHeight()}px";
        }
    }

    private void UpdateSectionGapLabels()
    {
        var usesLargeGap = SectionPatternCombo.SelectedIndex == 0;
        SmallGapXText.Text = $"Small gap X {ReadSmallGapX():0}px";
        SmallGapYText.Text = $"Small gap Y {ReadSmallGapY():0}px";
        LargeGapText.Text = usesLargeGap
            ? $"Large gap {ReadLargeGap():0}px"
            : "Large gap unused";
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
    }

    private static string GetSectionPatternName(int index) =>
        index == 1 ? SectionPattern.Vertical().Name : SectionPattern.TopGrouped().Name;

    private void UpdateLayoutSummary()
    {
        LayoutSummaryText.Text =
            $"Slots: {_overlaySlots.Count} | Canvas: {_layoutCanvasWidth:0}x{_layoutCanvasHeight:0} | " +
            $"Screen: {_overlayLeft:0}, {_overlayTop:0} | Opacity: {_overlayOpacity:0.00} | " +
            $"Scale: {_layoutSlotScale:0.0}x | Grid: {_layoutGridSnapSize:0}px | Hotkey: {_stopHotkey} | Max FPS: {_refreshFps}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _log.Info($"Status: {message}");
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

    private sealed record CaptureBackendOption(CaptureBackend Backend, string Label);

    private sealed record SectionSettings(double SmallGapX, double SmallGapY, double LargeGap);

    private sealed record CandidateEditSnapshot(
        List<CandidateState> Candidates,
        List<SectionState> Sections,
        int SelectedSectionId,
        int NextSectionId,
        int SelectedId);

    private sealed record CandidateState(int Id, double X, double Y, double Width, double Height, double Score, bool IsSelected);

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
