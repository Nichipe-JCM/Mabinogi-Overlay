using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shapes;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow : Window
{
    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly SlotDetectionService _slotDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<SlotCandidate> _candidates = new();
    private readonly List<OverlaySlot> _overlaySlots = new();
    private readonly Dictionary<SlotCandidate, Rectangle> _candidateRects = new();
    private readonly Dictionary<Image, OverlaySlot> _layoutImages = new();
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
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
        _hotkeyService = new HotkeyService();
        _hotkeyService.Register(new WindowInteropHelper(this).Handle, StopOverlay);
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not GameWindowInfo window)
        {
            SetStatus("먼저 게임 창을 선택해 주세요.");
            return;
        }

        if (!window.LooksLikeMabinogi)
        {
            SetStatus("선택한 창이 마비노기 창으로 확인되지 않아 캡처하지 않았습니다.");
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
            SetStatus("마비노기 창 확인 후 캡처했습니다. 후보 탐지를 실행해 주세요.");
        }
        catch (Exception ex)
        {
            SetStatus($"캡처 실패: {ex.Message}");
        }
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("캡처 이미지가 없습니다.");
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

        SetStatus($"{_candidates.Count}개 슬롯 후보를 찾았습니다. 사용할 슬롯만 체크한 뒤 배치해 주세요.");
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("먼저 캡처와 후보 선택을 진행해 주세요.");
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

        SetStatus($"{_overlaySlots.Count}개 슬롯을 오버레이 캔버스에 배치했습니다. 드래그로 위치를 조정할 수 있습니다.");
    }

    private void ClearCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        _candidates.Clear();
        ClearCandidateRects();
        SetStatus("후보를 비웠습니다.");
    }

    private void ApplyCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCanvasSize();
        SetStatus("오버레이 캔버스 크기를 적용했습니다.");
    }

    private void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("오버레이에 배치된 슬롯이 없습니다.");
            return;
        }

        StopOverlay();
        ApplyCanvasSize();
        var opacity = Math.Clamp(OpacitySlider.Value, 0.2, 1);
        _overlayWindow = new OverlayWindow(LayoutCanvas.Width, LayoutCanvas.Height, opacity, _overlaySlots)
        {
            Left = Math.Max(0, SystemParameters.WorkArea.Right - LayoutCanvas.Width - 40),
            Top = Math.Max(0, SystemParameters.WorkArea.Top + 120)
        };
        _overlayWindow.Show();
        _liveOverlayTimer.Start();
        SetStatus("오버레이를 시작했습니다. 오버레이는 클릭을 먹지 않으며 Ctrl+Shift+F8 또는 중지 버튼으로 끌 수 있습니다.");
    }

    private void StopOverlayButton_Click(object sender, RoutedEventArgs e) => StopOverlay();

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
        var captureStatus = _wgcSupport.IsSupported()
            ? "WGC 지원 가능"
            : "WGC 미지원 또는 비활성";
        WindowStatusText.Text = WindowCombo.SelectedItem is GameWindowInfo selected
            ? selected.LooksLikeMabinogi ? $"마비노기 창으로 확인되었습니다. ({captureStatus})" : $"마비노기 창으로 확인되지 않았습니다. ({captureStatus})"
            : "선택 가능한 창이 없습니다.";
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

    private void StopOverlay()
    {
        _liveOverlayTimer.Stop();
        _overlayWindow?.Close();
        _overlayWindow = null;
        SetStatus("오버레이를 중지했습니다.");
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
            SetStatus($"라이브 오버레이 갱신 실패: {ex.Message}");
        }
    }

    private void ApplyCanvasSize()
    {
        LayoutCanvas.Width = ReadPositiveDouble(OverlayWidthBox.Text, 360);
        LayoutCanvas.Height = ReadPositiveDouble(OverlayHeightBox.Text, 160);
    }

    private static double ReadPositiveDouble(string text, double fallback) =>
        double.TryParse(text, out var value) && value > 0 ? value : fallback;

    private void UpdateSizeLabels()
    {
        MinSlotSizeText.Text = $"{(int)MinSlotSizeSlider.Value}px";
        MaxSlotSizeText.Text = $"{(int)MaxSlotSizeSlider.Value}px";
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
