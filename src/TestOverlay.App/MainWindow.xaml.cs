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
    private readonly Dictionary<Image, OverlaySlot> _layoutImages = new();
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
    private WgcSelectionResult? _wgcSelection;
    private OverlayWindow? _overlayWindow;
    private Image? _draggingImage;
    private Point _dragOffset;
    private SlotCandidate? _draggingCandidate;
    private Point _candidateDragOffset;
    private Point _candidateDragStartPosition;
    private bool _candidateDragMoved;
    private bool _isLiveRefreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new { Candidates = _candidates };
        SlotSizeSlider.ValueChanged += (_, _) => UpdateSizeLabels();
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => StopOverlay(setStatus: false);
        UpdateSizeLabels();
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

        var slotSize = ReadSlotSize();
        foreach (var candidate in _slotDetection.Detect(_capturedImage, slotSize, slotSize))
        {
            AddCandidate(candidate);
        }

        _log.Info($"Slot detection completed: count={_candidates.Count}, slotSize={slotSize}");
        SetStatus($"Detected {_candidates.Count} slot candidates. Check only the slots to use, then place them.");
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture and detect candidates first.");
            return;
        }

        ApplyCanvasSize();
        _overlaySlots.Clear();
        _layoutImages.Clear();
        LayoutCanvas.Children.Clear();

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
            var size = Math.Max(24, candidate.SourceRect.Width * 1.5);
            if (cursorX + size > LayoutCanvas.Width - 8)
            {
                cursorX = 8;
                cursorY += rowHeight + 8;
                rowHeight = 0;
            }

            var rect = new Rect(cursorX, cursorY, size, size);
            var slot = new OverlaySlot(candidate, rect, crop);
            _overlaySlots.Add(slot);
            AddLayoutImage(slot);
            cursorX += size + 8;
            rowHeight = Math.Max(rowHeight, size);
        }

        _log.Info($"Placed overlay slots: count={_overlaySlots.Count}, canvas={LayoutCanvas.Width}x{LayoutCanvas.Height}");
        SetStatus($"Placed {_overlaySlots.Count} slots on the overlay canvas. Drag them to adjust positions.");
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

        var size = ReadSlotSize();
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

    private void DeleteSelectedCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedCandidates();
    }

    private void CandidateList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        DeleteSelectedCandidates();
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
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
        RenderLayoutPreview();
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
        SetStatus("Candidate list cleared.");
    }

    private void ApplyCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCanvasSize();
        SetStatus("Overlay canvas size applied.");
    }

    private void OpenLayoutEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("No overlay slots are placed.");
            return;
        }

        ApplyCanvasSize();
        var editor = new LayoutEditorWindow(LayoutCanvas.Width, LayoutCanvas.Height, _overlaySlots)
        {
            Owner = this
        };
        editor.ShowDialog();
        RenderLayoutPreview();
        SetStatus("Layout editor closed. Preview updated.");
    }

    private void ClearLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        ClearLayout();
        SetStatus("Overlay layout cleared.");
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCanvasSize();
        var profile = new OverlayProfile
        {
            CanvasWidth = LayoutCanvas.Width,
            CanvasHeight = LayoutCanvas.Height,
            ScreenLeft = ReadDouble(OverlayLeftBox.Text, 120),
            ScreenTop = ReadDouble(OverlayTopBox.Text, 120),
            Opacity = Math.Clamp(OpacitySlider.Value, 0.2, 1),
            StopHotkey = HotkeyBox.Text,
            RefreshIntervalMs = ReadPositiveInt(RefreshIntervalBox.Text, 500),
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

        OverlayWidthBox.Text = profile.CanvasWidth.ToString("0");
        OverlayHeightBox.Text = profile.CanvasHeight.ToString("0");
        OverlayLeftBox.Text = profile.ScreenLeft.ToString("0");
        OverlayTopBox.Text = profile.ScreenTop.ToString("0");
        OpacitySlider.Value = Math.Clamp(profile.Opacity, 0.2, 1);
        HotkeyBox.Text = profile.StopHotkey;
        RefreshIntervalBox.Text = Math.Max(100, profile.RefreshIntervalMs).ToString();
        ApplyCanvasSize();

        _overlaySlots.Clear();
        _layoutImages.Clear();
        LayoutCanvas.Children.Clear();
        _candidates.Clear();
        ClearCandidateRects();

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
            AddLayoutImage(slot);
        }

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
            ApplyCanvasSize();
            if (!RegisterStopHotkey())
            {
                return;
            }

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ReadPositiveInt(RefreshIntervalBox.Text, 500), 100, 5000));
            var opacity = Math.Clamp(OpacitySlider.Value, 0.2, 1);
            _overlayWindow = new OverlayWindow(LayoutCanvas.Width, LayoutCanvas.Height, opacity, _overlaySlots)
            {
                Left = ReadDouble(OverlayLeftBox.Text, SystemParameters.WorkArea.Right - LayoutCanvas.Width - 40),
                Top = ReadDouble(OverlayTopBox.Text, SystemParameters.WorkArea.Top + 120)
            };
            _overlayWindow.Show();
            _liveOverlayTimer.Start();
            if (_overlayWindow.ClickThroughConfigurationException is not null)
            {
                _log.Error("Overlay click-through configuration failed.", _overlayWindow.ClickThroughConfigurationException);
            }

            var clickThroughStatus = _overlayWindow.IsClickThroughConfigured ? "click-through" : "not click-through";
            _log.Info($"Overlay started: size={LayoutCanvas.Width}x{LayoutCanvas.Height}, left={_overlayWindow.Left}, top={_overlayWindow.Top}, opacity={opacity}, slots={_overlaySlots.Count}, hotkey={HotkeyBox.Text}, refreshMs={_liveOverlayTimer.Interval.TotalMilliseconds}, exStyle=0x{_overlayWindow.AppliedExtendedStyle:X8}, clickThrough={_overlayWindow.IsClickThroughConfigured}, noActivate={_overlayWindow.IsNoActivateConfigured}, topmost={_overlayWindow.IsTopmostConfigured}");
            SetStatus($"Overlay started ({clickThroughStatus}). Stop hotkey: {HotkeyBox.Text}");
        }
        catch (Exception ex)
        {
            _log.Error("Overlay start failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus($"Overlay start failed: {ex.Message}");
        }
    }

    private void StopOverlayButton_Click(object sender, RoutedEventArgs e) => StopOverlay();

    private void UseDefaultOverlayPositionButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCanvasSize();
        OverlayLeftBox.Text = Math.Max(0, SystemParameters.WorkArea.Right - LayoutCanvas.Width - 40).ToString("0");
        OverlayTopBox.Text = Math.Max(0, SystemParameters.WorkArea.Top + 120).ToString("0");
        SetStatus("Default overlay screen position applied.");
    }

    private void CaptureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CaptureCanvas);
        var hit = _candidates.FirstOrDefault(candidate => candidate.SourceRect.Contains(position));
        if (hit is not null)
        {
            hit.IsSelected = !hit.IsSelected;
            SelectCandidateInList(hit);
        }
    }

    private void CaptureCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(CaptureCanvas);
        if (Math.Abs(position.X - _candidateDragStartPosition.X) > 3 ||
            Math.Abs(position.Y - _candidateDragStartPosition.Y) > 3)
        {
            _candidateDragMoved = true;
        }

        var x = Math.Clamp(position.X - _candidateDragOffset.X, 0, Math.Max(0, CaptureCanvas.Width - _draggingCandidate.SourceRect.Width));
        var y = Math.Clamp(position.Y - _candidateDragOffset.Y, 0, Math.Max(0, CaptureCanvas.Height - _draggingCandidate.SourceRect.Height));
        _draggingCandidate.MoveTo(x, y);
        if (_candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
        }
    }

    private void CaptureCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            rect.ReleaseMouseCapture();
            if (!_candidateDragMoved)
            {
                _draggingCandidate.IsSelected = !_draggingCandidate.IsSelected;
                SelectCandidateInList(_draggingCandidate);
            }
        }

        _draggingCandidate = null;
        _candidateDragMoved = false;
    }

    private void LayoutCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingImage is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(LayoutCanvas);
        var x = Math.Clamp(position.X - _dragOffset.X, 0, Math.Max(0, LayoutCanvas.Width - _draggingImage.Width));
        var y = Math.Clamp(position.Y - _dragOffset.Y, 0, Math.Max(0, LayoutCanvas.Height - _draggingImage.Height));
        Canvas.SetLeft(_draggingImage, x);
        Canvas.SetTop(_draggingImage, y);

        if (_layoutImages.TryGetValue(_draggingImage, out var slot))
        {
            slot.OverlayRect = new Rect(x, y, _draggingImage.Width, _draggingImage.Height);
        }
    }

    private void LayoutCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingImage?.ReleaseMouseCapture();
        _draggingImage = null;
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
            _draggingCandidate = candidate;
            _candidateDragOffset = args.GetPosition(rect);
            _candidateDragStartPosition = args.GetPosition(CaptureCanvas);
            _candidateDragMoved = false;
            rect.CaptureMouse();
            SelectCandidateInList(candidate);
            args.Handled = true;
        };
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        Canvas.SetLeft(rect, candidate.SourceRect.X);
        Canvas.SetTop(rect, candidate.SourceRect.Y);
        UpdateCandidateVisual(candidate);
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

    private void AddLayoutImage(OverlaySlot slot)
    {
        var image = new Image
        {
            Source = slot.Preview,
            Width = slot.OverlayRect.Width,
            Height = slot.OverlayRect.Height,
            Stretch = Stretch.Fill,
            Cursor = Cursors.SizeAll
        };
        image.MouseLeftButtonDown += (sender, args) =>
        {
            _draggingImage = (Image)sender;
            _dragOffset = args.GetPosition(_draggingImage);
            _draggingImage.CaptureMouse();
            args.Handled = true;
        };

        _layoutImages[image] = slot;
        LayoutCanvas.Children.Add(image);
        Canvas.SetLeft(image, slot.OverlayRect.X);
        Canvas.SetTop(image, slot.OverlayRect.Y);
    }

    private void RenderLayoutPreview()
    {
        _layoutImages.Clear();
        LayoutCanvas.Children.Clear();
        foreach (var slot in _overlaySlots)
        {
            AddLayoutImage(slot);
        }
    }

    private void ClearLayout()
    {
        _overlaySlots.Clear();
        _layoutImages.Clear();
        LayoutCanvas.Children.Clear();
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

    private async void LiveOverlayTimer_Tick(object? sender, EventArgs e)
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
            var liveCapture = _wgcSelection is not null
                ? await _wgcCaptureService.CaptureOnceAsync(_wgcSelection.Item, TimeSpan.FromSeconds(3))
                : _captureService.CaptureClientArea(_selectedWindow!);
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
        if (!HotkeyParser.TryParse(HotkeyBox.Text, out var hotkey))
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

    private void ApplyCanvasSize()
    {
        LayoutCanvas.Width = ReadPositiveDouble(OverlayWidthBox.Text, 360);
        LayoutCanvas.Height = ReadPositiveDouble(OverlayHeightBox.Text, 160);
    }

    private static double ReadPositiveDouble(string text, double fallback) =>
        double.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static int ReadPositiveInt(string text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static double ReadDouble(string text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;

    private int ReadSlotSize() => Math.Clamp((int)SlotSizeSlider.Value, 12, 180);

    private void UpdateSizeLabels()
    {
        SlotSizeText.Text = $"{ReadSlotSize()}px";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _log.Info($"Status: {message}");
    }
}
