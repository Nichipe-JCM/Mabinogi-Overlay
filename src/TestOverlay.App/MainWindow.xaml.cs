using System.Collections.ObjectModel;
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
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly WgcCaptureService _wgcCaptureService = new();
    private readonly SlotDetectionService _slotDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly ProfileStore _profileStore = new();
    private readonly AppLog _log = new();
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<SlotCandidate> _candidates = new();
    private readonly List<OverlaySlot> _overlaySlots = new();
    private readonly Dictionary<SlotCandidate, Rectangle> _candidateRects = new();
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
    private WgcSelectionResult? _wgcSelection;
    private OverlayWindow? _overlayWindow;
    private SlotCandidate? _draggingCandidate;
    private Point _candidateDragStartPosition;
    private Dictionary<SlotCandidate, Point> _candidateDragOrigins = new();
    private Rectangle? _selectionRect;
    private Point _selectionStartPosition;
    private bool _isSelectingCandidates;
    private ActiveSection? _activeSection;
    private bool _isLiveRefreshInProgress;
    private double _layoutCanvasWidth = 360;
    private double _layoutCanvasHeight = 160;
    private double _overlayLeft = 120;
    private double _overlayTop = 120;
    private double _overlayOpacity = 0.8;
    private string _stopHotkey = "Ctrl+Shift+F8";
    private int _refreshFps = 60;
    private double _layoutSlotScale = 1.5;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new { Candidates = _candidates };
        SlotSizeSlider.ValueChanged += (_, _) => UpdateSizeLabels();
        SectionPatternCombo.SelectionChanged += (_, _) =>
        {
            UpdateSectionGapLabels();
            RebuildActiveSection();
        };
        SmallGapXSlider.ValueChanged += (_, _) =>
        {
            UpdateSectionGapLabels();
            RebuildActiveSection();
        };
        SmallGapYSlider.ValueChanged += (_, _) =>
        {
            UpdateSectionGapLabels();
            RebuildActiveSection();
        };
        LargeGapSlider.ValueChanged += (_, _) =>
        {
            UpdateSectionGapLabels();
            RebuildActiveSection();
        };
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => StopOverlay(setStatus: false);
        UpdateSizeLabels();
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        _log.Info("Application loaded.");
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void VerifyWgcButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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
        }
        catch (Exception ex)
        {
            _log.Error("WGC verification failed.", ex);
            SetStatus($"WGC verification failed: {ex.Message}");
        }
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wgcSelection is null)
        {
            SetStatus("Verify the Mabinogi window with WGC before capture.");
            return;
        }

        if (!_wgcSelection.LooksLikeMabinogi)
        {
            SetStatus("WGC verification selected a non-Mabinogi window. Verify WGC again or refresh the window list.");
            return;
        }

        try
        {
            CaptureButton.IsEnabled = false;
            _capturedImage = await _wgcCaptureService.CaptureOnceAsync(_wgcSelection.Item, TimeSpan.FromSeconds(3));
            _selectedWindow = WindowCombo.SelectedItem as GameWindowInfo;
            _log.Info($"WGC capture succeeded: {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}, item={_wgcSelection.DisplayName}");
            CaptureImage.Source = _capturedImage;
            CaptureCanvas.Width = _capturedImage.PixelWidth;
            CaptureCanvas.Height = _capturedImage.PixelHeight;
            CaptureInfoText.Text = $"{_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}";
            _candidates.Clear();
            ClearCandidateRects();
            _activeSection = null;
            SetStatus("Captured the verified Mabinogi window with WGC. Run slot detection next.");
        }
        catch (Exception ex)
        {
            _log.Error("Capture failed.", ex);
            SetStatus($"Capture failed: {ex.Message}");
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        _candidates.Clear();
        ClearCandidateRects();
        _activeSection = null;

        var slotSize = ReadCandidateBoxSize();
        foreach (var candidate in _slotDetection.Detect(_capturedImage, slotSize, slotSize))
        {
            AddCandidate(candidate);
        }

        _log.Info($"Slot detection completed: count={_candidates.Count}, innerSlotSize={ReadSlotInnerSize()}, candidateBoxSize={slotSize}");
        SetStatus($"Detected {_candidates.Count} slot candidates. Check only the slots to use, then place them.");
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture and detect candidates first.");
            return;
        }

        _overlaySlots.Clear();

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No checked candidates. Check slots to place first.");
            return;
        }

        var cursorX = 8.0;
        var cursorY = 8.0;
        var rowHeight = 0.0;
        foreach (var candidate in selected)
        {
            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var size = Math.Max(16, candidate.SourceRect.Width * ReadLayoutSlotScale());
            if (cursorX + size > _layoutCanvasWidth - 8)
            {
                cursorX = 8;
                cursorY += rowHeight + 8;
                rowHeight = 0;
            }

            var rect = new Rect(cursorX, cursorY, size, size);
            var slot = new OverlaySlot(candidate, rect, crop);
            _overlaySlots.Add(slot);
            cursorX += size + 8;
            rowHeight = Math.Max(rowHeight, size);
        }

        var requiredHeight = cursorY + rowHeight + 8;
        if (requiredHeight > _layoutCanvasHeight)
        {
            _layoutCanvasHeight = requiredHeight;
        }

        UpdateLayoutSummary();
        _log.Info($"Placed overlay slots: count={_overlaySlots.Count}, canvas={_layoutCanvasWidth}x{_layoutCanvasHeight}");
        SetStatus($"Placed {_overlaySlots.Count} slots. Open Manage Layout to arrange them.");
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

        var size = ReadCandidateBoxSize();
        var source = CandidateList.SelectedItem is SlotCandidate selected
            ? new Rect(
                Math.Clamp(selected.SourceRect.X + selected.SourceRect.Width + 4, 0, Math.Max(0, CaptureCanvas.Width - size)),
                Math.Clamp(selected.SourceRect.Y, 0, Math.Max(0, CaptureCanvas.Height - size)),
                size,
                size)
            : new Rect(8, 8, size, size);

        var candidate = new SlotCandidate(NextCandidateId(), source, 100);
        AddCandidate(candidate);
        CandidateList.SelectedItem = candidate;
        SetStatus("Manual candidate added. Drag it over the target quickslot.");
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

        var pattern = ReadSectionPattern();
        var added = AddSectionCandidates(seed, pattern);
        _log.Info($"Quickslot section generated: pattern={pattern.Name}, seed={seed.Id}, added={added}");
        SetStatus($"Generated {pattern.Name} from #{seed.Id:000}. Adjust small/large gap sliders to align the section.");
    }

    private void DeleteSelectedCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedCandidates();
    }

    private void CandidateList_KeyDown(object sender, KeyEventArgs e)
    {
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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
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

    private void DeleteSelectedCandidates()
    {
        var selected = GetCandidatesToDelete();
        if (selected.Count == 0)
        {
            SetStatus("No selected candidates to delete.");
            return;
        }

        foreach (var candidate in selected)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }

            _candidates.Remove(candidate);
        }

        _overlaySlots.RemoveAll(slot => selected.Contains(slot.Source));
        if (_activeSection is not null && selected.Any(candidate => _activeSection.Candidates.Contains(candidate)))
        {
            _activeSection = null;
        }

        UpdateLayoutSummary();
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
        _candidates.Clear();
        ClearCandidateRects();
        _activeSection = null;
        SetStatus("Candidate list cleared.");
    }

    private void OpenLayoutEditorButton_Click(object sender, RoutedEventArgs e)
    {
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
        UpdateLayoutSummary();
        SetStatus("Layout editor closed. Overlay settings updated.");
    }

    private void ClearLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        ClearLayout();
        SetStatus("Overlay layout cleared.");
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = new OverlayProfile
        {
            CanvasWidth = _layoutCanvasWidth,
            CanvasHeight = _layoutCanvasHeight,
            ScreenLeft = _overlayLeft,
            ScreenTop = _overlayTop,
            Opacity = _overlayOpacity,
            StopHotkey = _stopHotkey,
            RefreshIntervalMs = RefreshIntervalFromFps(_refreshFps),
            RefreshFps = _refreshFps,
            LayoutSlotScale = ReadLayoutSlotScale(),
            Slots = _overlaySlots.Select(slot => new OverlayProfileSlot
            {
                SourceX = slot.Source.SourceRect.X,
                SourceY = slot.Source.SourceRect.Y,
                SourceWidth = slot.Source.SourceRect.Width,
                SourceHeight = slot.Source.SourceRect.Height,
                OverlayX = slot.OverlayRect.X,
                OverlayY = slot.OverlayRect.Y,
                OverlayWidth = slot.OverlayRect.Width,
                OverlayHeight = slot.OverlayRect.Height
            }).ToList()
        };

        _profileStore.Save(profile);
        _log.Info($"Profile saved: {_profileStore.DefaultProfilePath}, slots={profile.Slots.Count}");
        SetStatus($"Profile saved: {_profileStore.DefaultProfilePath}");
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before loading a profile.");
            return;
        }

        var profile = _profileStore.LoadDefault();
        if (profile is null)
        {
            SetStatus("No saved profile exists.");
            return;
        }

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

        _overlaySlots.Clear();
        _candidates.Clear();
        ClearCandidateRects();
        _activeSection = null;

        var id = 1;
        foreach (var savedSlot in profile.Slots)
        {
            var candidate = new SlotCandidate(
                id++,
                new Rect(savedSlot.SourceX, savedSlot.SourceY, savedSlot.SourceWidth, savedSlot.SourceHeight),
                100);
            AddCandidate(candidate);

            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop);
            _overlaySlots.Add(slot);
        }

        UpdateLayoutSummary();
        _log.Info($"Profile loaded: slots={profile.Slots.Count}");
        SetStatus($"Profile loaded: {profile.Slots.Count} slots.");
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

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(_refreshFps));
            if (_wgcSelection is not null)
            {
                _wgcCaptureService.StartLiveCapture(_wgcSelection.Item);
            }

            _overlayWindow = new OverlayWindow(_layoutCanvasWidth, _layoutCanvasHeight, _overlayOpacity, _overlaySlots)
            {
                Left = _overlayLeft,
                Top = _overlayTop
            };
            _overlayWindow.Show();
            _liveOverlayTimer.Start();
            if (_overlayWindow.ClickThroughConfigurationException is not null)
            {
                _log.Error("Overlay click-through configuration failed.", _overlayWindow.ClickThroughConfigurationException);
            }

            var clickThroughStatus = _overlayWindow.IsClickThroughConfigured ? "click-through" : "not click-through";
            _log.Info($"Overlay started: size={_layoutCanvasWidth}x{_layoutCanvasHeight}, left={_overlayWindow.Left}, top={_overlayWindow.Top}, opacity={_overlayOpacity}, slots={_overlaySlots.Count}, hotkey={_stopHotkey}, refreshFps={_refreshFps}, refreshMs={_liveOverlayTimer.Interval.TotalMilliseconds}, exStyle=0x{_overlayWindow.AppliedExtendedStyle:X8}, clickThrough={_overlayWindow.IsClickThroughConfigured}, noActivate={_overlayWindow.IsNoActivateConfigured}, topmost={_overlayWindow.IsTopmostConfigured}");
            SetStatus($"Overlay started ({clickThroughStatus}). Stop hotkey: {_stopHotkey}");
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
        var hit = _candidates.FirstOrDefault(candidate => candidate.SourceRect.Contains(position));
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
        if (_isSelectingCandidates)
        {
            EndCandidateBoxSelection();
            return;
        }

        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            rect.ReleaseMouseCapture();
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
    }

    private void RefreshWindows()
    {
        var windows = _windowDiscovery.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.LooksLikeMabinogi) ?? windows.FirstOrDefault();
        var captureStatus = _wgcSupport.IsSupported() ? "WGC supported" : "WGC unavailable";
        WindowStatusText.Text = WindowCombo.SelectedItem is GameWindowInfo selected
            ? selected.LooksLikeMabinogi
                ? $"Mabinogi window recognized. ({captureStatus})"
                : $"Selected window is not recognized as Mabinogi. ({captureStatus})"
            : "No selectable window found.";
    }

    private void AddCandidateVisual(SlotCandidate candidate)
    {
        var rect = new Rectangle
        {
            Width = candidate.SourceRect.Width,
            Height = candidate.SourceRect.Height,
            StrokeThickness = 2,
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
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        Canvas.SetLeft(rect, candidate.SourceRect.X);
        Canvas.SetTop(rect, candidate.SourceRect.Y);
        UpdateCandidateVisual(candidate);
    }

    private void BeginCandidateDrag(SlotCandidate candidate, Rectangle rect, MouseButtonEventArgs args)
    {
        var isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (isShiftPressed)
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
        _candidateDragOrigins = _candidates
            .Where(item => item.IsSelected)
            .ToDictionary(item => item, item => new Point(item.SourceRect.X, item.SourceRect.Y));
        rect.CaptureMouse();
    }

    private void BeginCandidateBoxSelection(Point position)
    {
        _isSelectingCandidates = true;
        _selectionStartPosition = position;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
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
                if (selection.IntersectsWith(candidate.SourceRect))
                {
                    candidate.IsSelected = true;
                }
            }

            CaptureCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;
        }

        CaptureCanvas.ReleaseMouseCapture();
        _isSelectingCandidates = false;
    }

    private void MoveSelectedCandidates(Point position)
    {
        if (_candidateDragOrigins.Count == 0)
        {
            return;
        }

        var requestedDeltaX = position.X - _candidateDragStartPosition.X;
        var requestedDeltaY = position.Y - _candidateDragStartPosition.Y;
        var minDeltaX = _candidateDragOrigins.Max(item => -item.Value.X);
        var minDeltaY = _candidateDragOrigins.Max(item => -item.Value.Y);
        var maxDeltaX = _candidateDragOrigins.Min(item => CaptureCanvas.Width - item.Value.X - item.Key.SourceRect.Width);
        var maxDeltaY = _candidateDragOrigins.Min(item => CaptureCanvas.Height - item.Value.Y - item.Key.SourceRect.Height);
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
        var clampedX = Math.Clamp(x, 0, Math.Max(0, CaptureCanvas.Width - candidate.SourceRect.Width));
        var clampedY = Math.Clamp(y, 0, Math.Max(0, CaptureCanvas.Height - candidate.SourceRect.Height));
        candidate.MoveTo(clampedX, clampedY);
        if (_candidateRects.TryGetValue(candidate, out var candidateRect))
        {
            Canvas.SetLeft(candidateRect, clampedX);
            Canvas.SetTop(candidateRect, clampedY);
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

    private int AddSectionCandidates(SlotCandidate seed, SectionPattern pattern)
    {
        var added = 0;
        var sectionCandidates = new List<SlotCandidate>();

        ClearCandidateSelection();
        foreach (var offset in BuildSectionOffsets(seed, pattern, ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap()))
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
        _activeSection = new ActiveSection(seed, pattern, sectionCandidates);
        return added;
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
        var innerPitchX = slotWidth + pattern.InnerGapX(smallGap);
        var innerPitchY = slotHeight + pattern.InnerGapY(smallGapY);
        var groupPitchX = pattern.GroupColumns * innerPitchX + pattern.GroupGapX(largeGap);
        var groupPitchY = pattern.GroupRows * innerPitchY + pattern.GroupGapY(largeGap);

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
        rect.X >= 0 &&
        rect.Y >= 0 &&
        rect.Right <= CaptureCanvas.Width &&
        rect.Bottom <= CaptureCanvas.Height;

    private SectionPattern ReadSectionPattern() =>
        SectionPatternCombo.SelectedIndex == 1
            ? SectionPattern.LeftVertical()
            : SectionPattern.TopGrouped();

    private void RebuildActiveSection()
    {
        if (_activeSection is null || _capturedImage is null)
        {
            return;
        }

        var offsets = BuildSectionOffsets(
                _activeSection.Seed,
                _activeSection.Pattern,
                ReadSmallGapX(),
                ReadSmallGapY(),
                ReadLargeGap())
            .ToList();
        var count = Math.Min(offsets.Count, _activeSection.Candidates.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = _activeSection.Candidates[i];
            var offset = offsets[i];
            var x = _activeSection.Seed.SourceRect.X + offset.X;
            var y = _activeSection.Seed.SourceRect.Y + offset.Y;
            MoveCandidate(candidate, x, y);
        }

        SetStatus($"Adjusted {_activeSection.Pattern.Name}: gap X {ReadSmallGapX():0}px, gap Y {ReadSmallGapY():0}px, large gap {ReadLargeGap():0}px.");
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

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate highlighted)
        {
            selected.Add(highlighted);
        }

        if (selected.Count == 0)
        {
            return false;
        }

        foreach (var candidate in selected)
        {
            MoveCandidate(
                candidate,
                candidate.SourceRect.X + delta.X,
                candidate.SourceRect.Y + delta.Y);
        }

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
    }

    private void ClearCandidateRects()
    {
        foreach (var rect in _candidateRects.Values)
        {
            CaptureCanvas.Children.Remove(rect);
        }

        _candidateRects.Clear();
    }

    private int NextCandidateId() => _candidates.Count == 0 ? 1 : _candidates.Max(candidate => candidate.Id) + 1;

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
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

    private void LiveOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if ((_wgcSelection is null && _selectedWindow is null) ||
            _overlayWindow is null ||
            _overlaySlots.Count == 0 ||
            _isLiveRefreshInProgress)
        {
            return;
        }

        try
        {
            _isLiveRefreshInProgress = true;
            BitmapSource liveCapture;
            if (_wgcSelection is not null)
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
            else
            {
                liveCapture = _captureService.CaptureClientArea(_selectedWindow!);
            }

            foreach (var slot in _overlaySlots)
            {
                slot.Preview = _captureService.Crop(liveCapture, slot.Source.SourceRect);
            }

            _overlayWindow.RenderSlots(_overlaySlots);
        }
        catch (Exception ex)
        {
            _log.Error("Live overlay refresh failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus($"Live overlay refresh failed: {ex.Message}");
        }
        finally
        {
            _isLiveRefreshInProgress = false;
        }
    }

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

    private int ReadSlotInnerSize() => Math.Clamp((int)SlotSizeSlider.Value, 12, 180);

    private int ReadCandidateBoxSize() => ReadSlotInnerSize() + CandidateBorderPixels * 2;

    private double ReadLayoutSlotScale() => Math.Clamp(_layoutSlotScale, 1, 3);

    private double ReadSmallGapX() => Math.Clamp(SmallGapXSlider.Value, 0, 8);

    private double ReadSmallGapY() => Math.Clamp(SmallGapYSlider.Value, 0, 8);

    private double ReadLargeGap() => Math.Clamp(LargeGapSlider.Value, 0, 32);

    private static int CoerceRefreshFps(int fps) =>
        RefreshFpsOptions.OrderBy(option => Math.Abs(option - fps)).First();

    private static int RefreshIntervalFromFps(int fps) =>
        (int)Math.Max(1, Math.Round(1000.0 / CoerceRefreshFps(fps)));

    private static int FpsFromInterval(int intervalMs) =>
        intervalMs <= 0 ? 60 : (int)Math.Round(1000.0 / intervalMs);

    private void UpdateSizeLabels()
    {
        SlotSizeText.Text = $"inside {ReadSlotInnerSize()}px, box {ReadCandidateBoxSize()}px";
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

    private void UpdateLayoutSummary()
    {
        LayoutSummaryText.Text =
            $"Slots: {_overlaySlots.Count} | Canvas: {_layoutCanvasWidth:0}x{_layoutCanvasHeight:0} | " +
            $"Screen: {_overlayLeft:0}, {_overlayTop:0} | Opacity: {_overlayOpacity:0.00} | " +
            $"Scale: {_layoutSlotScale:0.0}x | Hotkey: {_stopHotkey} | Max FPS: {_refreshFps}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _log.Info($"Status: {message}");
    }

    private sealed record ActiveSection(SlotCandidate Seed, SectionPattern Pattern, List<SlotCandidate> Candidates);

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

        public static SectionPattern LeftVertical() => new(
            "left vertical 2x8",
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
