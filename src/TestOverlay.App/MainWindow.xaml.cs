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
    private readonly SlotDetectionService _slotDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly ProfileStore _profileStore = new();
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new { Candidates = _candidates };
        MinSlotSizeSlider.ValueChanged += (_, _) => UpdateSizeLabels();
        MaxSlotSizeSlider.ValueChanged += (_, _) => UpdateSizeLabels();
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _hotkeyService?.Dispose();
        UpdateSizeLabels();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RegisterStopHotkey();
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
                return;
            }

            _wgcSelection = result;
            SetStatus(result.LooksLikeMabinogi
                ? $"WGC verified Mabinogi window: {result.DisplayName} ({result.Width}x{result.Height})"
                : $"WGC selected window is not recognized as Mabinogi: {result.DisplayName} ({result.Width}x{result.Height})");
        }
        catch (Exception ex)
        {
            SetStatus($"WGC verification failed: {ex.Message}");
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not GameWindowInfo window)
        {
            SetStatus("Select a game window first.");
            return;
        }

        if (!window.LooksLikeMabinogi)
        {
            SetStatus("The selected window is not recognized as Mabinogi. Capture was skipped.");
            return;
        }

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
            _capturedImage = _captureService.CaptureClientArea(window);
            _selectedWindow = window;
            CaptureImage.Source = _capturedImage;
            CaptureCanvas.Width = _capturedImage.PixelWidth;
            CaptureCanvas.Height = _capturedImage.PixelHeight;
            CaptureInfoText.Text = $"{_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}";
            _candidates.Clear();
            ClearCandidateRects();
            SetStatus("Captured the verified Mabinogi window. Run slot detection next.");
        }
        catch (Exception ex)
        {
            SetStatus($"Capture failed: {ex.Message}");
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

        var min = (int)MinSlotSizeSlider.Value;
        var max = Math.Max(min, (int)MaxSlotSizeSlider.Value);
        foreach (var candidate in _slotDetection.Detect(_capturedImage, min, max))
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

        SetStatus($"Placed {_overlaySlots.Count} slots on the overlay canvas. Drag them to adjust positions.");
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
            _candidates.Add(candidate);
            AddCandidateVisual(candidate);

            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop);
            _overlaySlots.Add(slot);
            AddLayoutImage(slot);
        }

        SetStatus($"Profile loaded: {profile.Slots.Count} slots.");
    }

    private void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("No slots are placed on the overlay canvas.");
            return;
        }

        if (!RegisterStopHotkey())
        {
            return;
        }

        StopOverlay(setStatus: false);
        ApplyCanvasSize();
        var opacity = Math.Clamp(OpacitySlider.Value, 0.2, 1);
        _overlayWindow = new OverlayWindow(LayoutCanvas.Width, LayoutCanvas.Height, opacity, _overlaySlots)
        {
            Left = ReadDouble(OverlayLeftBox.Text, SystemParameters.WorkArea.Right - LayoutCanvas.Width - 40),
            Top = ReadDouble(OverlayTopBox.Text, SystemParameters.WorkArea.Top + 120)
        };
        _overlayWindow.Show();
        _liveOverlayTimer.Start();
        SetStatus($"Overlay started. It is click-through. Stop hotkey: {HotkeyBox.Text}");
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
            CandidateList.SelectedItem = hit;
        }
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
            IsHitTestVisible = false
        };
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        Canvas.SetLeft(rect, candidate.SourceRect.X);
        Canvas.SetTop(rect, candidate.SourceRect.Y);
        UpdateCandidateVisual(candidate);
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

    private void ClearCandidateRects()
    {
        foreach (var rect in _candidateRects.Values)
        {
            CaptureCanvas.Children.Remove(rect);
        }

        _candidateRects.Clear();
    }

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
        _overlayWindow?.Close();
        _overlayWindow = null;
        if (setStatus)
        {
            SetStatus("Overlay stopped.");
        }
    }

    private void LiveOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_selectedWindow is null || _overlayWindow is null || _overlaySlots.Count == 0)
        {
            return;
        }

        try
        {
            var liveCapture = _captureService.CaptureClientArea(_selectedWindow);
            foreach (var slot in _overlaySlots)
            {
                slot.Preview = _captureService.Crop(liveCapture, slot.Source.SourceRect);
            }

            _overlayWindow.RenderSlots(_overlaySlots);
        }
        catch (Exception ex)
        {
            _liveOverlayTimer.Stop();
            SetStatus($"Live overlay refresh failed: {ex.Message}");
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
            SetStatus($"Stop hotkey registration failed: {hotkey.DisplayText}");
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

    private static double ReadDouble(string text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;

    private void UpdateSizeLabels()
    {
        MinSlotSizeText.Text = $"{(int)MinSlotSizeSlider.Value}px";
        MaxSlotSizeText.Text = $"{(int)MaxSlotSizeSlider.Value}px";
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
