using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class LayoutEditorWindow : Window
{
    private readonly IReadOnlyList<OverlaySlot> _slots;
    private readonly Dictionary<Image, OverlaySlot> _images = new();
    private readonly Dictionary<OverlaySlot, double> _sourceSizes = new();
    private static readonly int[] FpsOptions = [30, 60, 120, 144];
    private readonly Rectangle _overlayPreviewRect = new();
    private Image? _draggingImage;
    private OverlayPlacementPreviewWindow? _placementPreviewWindow;
    private bool _isDraggingOverlayPreview;
    private Point _dragOffset;

    public LayoutEditorWindow(
        double canvasWidth,
        double canvasHeight,
        double screenLeft,
        double screenTop,
        double opacity,
        string hotkey,
        int refreshFps,
        double slotScale,
        IReadOnlyList<OverlaySlot> slots)
    {
        InitializeComponent();
        _slots = slots;
        CanvasWidth = Math.Max(120, canvasWidth);
        CanvasHeight = Math.Max(80, canvasHeight);
        ScreenLeft = screenLeft;
        ScreenTop = screenTop;
        OverlayOpacity = Math.Clamp(opacity, 0.2, 1);
        StopHotkey = hotkey;
        RefreshFps = CoerceFps(refreshFps);
        SlotScale = Math.Clamp(slotScale, 1, 3);
        foreach (var slot in _slots)
        {
            _sourceSizes[slot] = Math.Max(1, slot.Source.SourceRect.Width);
        }

        SlotScaleSlider.ValueChanged += (_, _) =>
        {
            SlotScaleText.Text = $"{SlotScaleSlider.Value:0.0}x";
            ResizeSlotsToScale(SlotScaleSlider.Value);
        };
        PopulateControls();
        InitializeOverlayPreview();
        RenderSlots();
    }

    public double CanvasWidth { get; private set; }

    public double CanvasHeight { get; private set; }

    public double ScreenLeft { get; private set; }

    public double ScreenTop { get; private set; }

    public double OverlayOpacity { get; private set; }

    public string StopHotkey { get; private set; } = "Ctrl+Shift+F8";

    public int RefreshFps { get; private set; }

    public double SlotScale { get; private set; }

    private void PopulateControls()
    {
        CanvasWidthBox.Text = CanvasWidth.ToString("0");
        CanvasHeightBox.Text = CanvasHeight.ToString("0");
        ApplyCanvasSize();
        ScreenLeftBox.Text = ScreenLeft.ToString("0");
        ScreenTopBox.Text = ScreenTop.ToString("0");
        OpacitySlider.Value = OverlayOpacity;
        HotkeyBox.Text = StopHotkey;
        RefreshFpsCombo.ItemsSource = FpsOptions;
        RefreshFpsCombo.SelectedItem = RefreshFps;
        SlotScaleSlider.Value = SlotScale;
        SlotScaleText.Text = $"{SlotScale:0.0}x";
    }

    private void InitializeOverlayPreview()
    {
        _overlayPreviewRect.Stroke = Brushes.DeepSkyBlue;
        _overlayPreviewRect.StrokeThickness = 2;
        _overlayPreviewRect.Fill = new VisualBrush(EditorCanvas)
        {
            Opacity = 0.85,
            Stretch = Stretch.Fill
        };
        _overlayPreviewRect.Cursor = Cursors.SizeAll;
        _overlayPreviewRect.MouseLeftButtonDown += OverlayPreviewRect_MouseLeftButtonDown;
        ScreenPreviewCanvas.Children.Add(_overlayPreviewRect);
        ScreenPreviewCanvas.SizeChanged += (_, _) => UpdateOverlayPreview();
        UpdateOverlayPreview();
    }

    private void RenderSlots()
    {
        EditorCanvas.Children.Clear();
        _images.Clear();

        foreach (var slot in _slots)
        {
            var image = new Image
            {
                Source = slot.Preview,
                Width = slot.OverlayRect.Width,
                Height = slot.OverlayRect.Height,
                Stretch = Stretch.Fill,
                Cursor = Cursors.SizeAll
            };
            image.MouseLeftButtonDown += SlotImage_MouseLeftButtonDown;
            _images[image] = slot;
            EditorCanvas.Children.Add(image);
            Canvas.SetLeft(image, slot.OverlayRect.X);
            Canvas.SetTop(image, slot.OverlayRect.Y);
        }
    }

    private void SlotImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingImage = (Image)sender;
        _dragOffset = e.GetPosition(_draggingImage);
        _draggingImage.CaptureMouse();
        e.Handled = true;
    }

    private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingImage is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(EditorCanvas);
        var x = Math.Clamp(position.X - _dragOffset.X, 0, Math.Max(0, EditorCanvas.Width - _draggingImage.Width));
        var y = Math.Clamp(position.Y - _dragOffset.Y, 0, Math.Max(0, EditorCanvas.Height - _draggingImage.Height));
        Canvas.SetLeft(_draggingImage, x);
        Canvas.SetTop(_draggingImage, y);

        if (_images.TryGetValue(_draggingImage, out var slot))
        {
            slot.OverlayRect = new Rect(x, y, _draggingImage.Width, _draggingImage.Height);
        }
    }

    private void EditorCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingImage?.ReleaseMouseCapture();
        _draggingImage = null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromControls();
        ClampSlotsToCanvas();
        RenderSlots();
    }

    private void DefaultPositionButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromControls();
        ScreenLeft = Math.Max(0, SystemParameters.WorkArea.Right - CanvasWidth - 40);
        ScreenTop = Math.Max(0, SystemParameters.WorkArea.Top + 120);
        ScreenLeftBox.Text = ScreenLeft.ToString("0");
        ScreenTopBox.Text = ScreenTop.ToString("0");
        UpdateOverlayPreview();
    }

    private void ScreenPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromControls();
        _placementPreviewWindow?.Close();
        _placementPreviewWindow = new OverlayPlacementPreviewWindow(
            ScreenLeft,
            ScreenTop,
            CanvasWidth,
            CanvasHeight,
            OverlayOpacity,
            _slots,
            ApplyPlacementFromPreview)
        {
            Owner = this
        };
        _placementPreviewWindow.Closed += (_, _) => _placementPreviewWindow = null;
        _placementPreviewWindow.Show();
    }

    private void ApplyPlacementFromPreview(double left, double top, double width, double height)
    {
        ScreenLeft = left;
        ScreenTop = top;
        CanvasWidth = Math.Max(120, width);
        CanvasHeight = Math.Max(80, height);
        ScreenLeftBox.Text = ScreenLeft.ToString("0");
        ScreenTopBox.Text = ScreenTop.ToString("0");
        CanvasWidthBox.Text = CanvasWidth.ToString("0");
        CanvasHeightBox.Text = CanvasHeight.ToString("0");
        ApplyCanvasSize();
        ClampSlotsToCanvas();
        RenderSlots();
        UpdateOverlayPreview();
    }

    private void ApplySettingsFromControls()
    {
        CanvasWidth = ReadPositiveDouble(CanvasWidthBox.Text, CanvasWidth);
        CanvasHeight = ReadPositiveDouble(CanvasHeightBox.Text, CanvasHeight);
        ScreenLeft = ReadDouble(ScreenLeftBox.Text, ScreenLeft);
        ScreenTop = ReadDouble(ScreenTopBox.Text, ScreenTop);
        OverlayOpacity = Math.Clamp(OpacitySlider.Value, 0.2, 1);
        StopHotkey = HotkeyBox.Text;
        RefreshFps = RefreshFpsCombo.SelectedItem is int selectedFps
            ? selectedFps
            : CoerceFps(RefreshFps);
        SlotScale = Math.Clamp(SlotScaleSlider.Value, 1, 3);
        ApplyCanvasSize();
        UpdateOverlayPreview();
    }

    private void ApplyCanvasSize()
    {
        EditorCanvas.Width = CanvasWidth;
        EditorCanvas.Height = CanvasHeight;
    }

    private void ResizeSlotsToScale(double scale)
    {
        foreach (var slot in _slots)
        {
            var sourceSize = _sourceSizes.TryGetValue(slot, out var savedSize)
                ? savedSize
                : Math.Max(1, slot.Source.SourceRect.Width);
            var size = Math.Max(16, sourceSize * scale);
            slot.OverlayRect = new Rect(slot.OverlayRect.X, slot.OverlayRect.Y, size, size);
        }

        ClampSlotsToCanvas();
        RenderSlots();
        UpdateOverlayPreview();
    }

    private void OverlayPreviewRect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingOverlayPreview = true;
        _dragOffset = e.GetPosition(_overlayPreviewRect);
        _overlayPreviewRect.CaptureMouse();
        e.Handled = true;
    }

    private void ScreenPreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingOverlayPreview || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var preview = GetPreviewGeometry();
        if (preview.Scale <= 0)
        {
            return;
        }

        var position = e.GetPosition(ScreenPreviewCanvas);
        var previewLeft = Math.Clamp(
            position.X - _dragOffset.X,
            preview.Left,
            Math.Max(preview.Left, preview.Left + preview.Width - _overlayPreviewRect.Width));
        var previewTop = Math.Clamp(
            position.Y - _dragOffset.Y,
            preview.Top,
            Math.Max(preview.Top, preview.Top + preview.Height - _overlayPreviewRect.Height));

        ScreenLeft = Math.Round(SystemParameters.WorkArea.Left + (previewLeft - preview.Left) / preview.Scale);
        ScreenTop = Math.Round(SystemParameters.WorkArea.Top + (previewTop - preview.Top) / preview.Scale);
        ScreenLeftBox.Text = ScreenLeft.ToString("0");
        ScreenTopBox.Text = ScreenTop.ToString("0");
        UpdateOverlayPreview();
    }

    private void ScreenPreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingOverlayPreview)
        {
            return;
        }

        _overlayPreviewRect.ReleaseMouseCapture();
        _isDraggingOverlayPreview = false;
    }

    private void UpdateOverlayPreview()
    {
        if (!ScreenPreviewCanvas.Children.Contains(_overlayPreviewRect))
        {
            return;
        }

        var preview = GetPreviewGeometry();
        if (preview.Scale <= 0)
        {
            return;
        }

        _overlayPreviewRect.Width = Math.Max(8, CanvasWidth * preview.Scale);
        _overlayPreviewRect.Height = Math.Max(8, CanvasHeight * preview.Scale);
        var left = preview.Left + (ScreenLeft - SystemParameters.WorkArea.Left) * preview.Scale;
        var top = preview.Top + (ScreenTop - SystemParameters.WorkArea.Top) * preview.Scale;
        Canvas.SetLeft(_overlayPreviewRect, Math.Clamp(left, preview.Left, Math.Max(preview.Left, preview.Left + preview.Width - _overlayPreviewRect.Width)));
        Canvas.SetTop(_overlayPreviewRect, Math.Clamp(top, preview.Top, Math.Max(preview.Top, preview.Top + preview.Height - _overlayPreviewRect.Height)));
    }

    private PreviewGeometry GetPreviewGeometry()
    {
        var availableWidth = Math.Max(1, ScreenPreviewCanvas.ActualWidth - 16);
        var availableHeight = Math.Max(1, ScreenPreviewCanvas.ActualHeight - 16);
        var screenWidth = Math.Max(1, SystemParameters.WorkArea.Width);
        var screenHeight = Math.Max(1, SystemParameters.WorkArea.Height);
        var scale = Math.Min(availableWidth / screenWidth, availableHeight / screenHeight);
        var width = screenWidth * scale;
        var height = screenHeight * scale;
        return new PreviewGeometry(
            (ScreenPreviewCanvas.ActualWidth - width) / 2,
            (ScreenPreviewCanvas.ActualHeight - height) / 2,
            width,
            height,
            scale);
    }

    private void ClampSlotsToCanvas()
    {
        foreach (var slot in _slots)
        {
            var x = Math.Clamp(slot.OverlayRect.X, 0, Math.Max(0, EditorCanvas.Width - slot.OverlayRect.Width));
            var y = Math.Clamp(slot.OverlayRect.Y, 0, Math.Max(0, EditorCanvas.Height - slot.OverlayRect.Height));
            slot.OverlayRect = new Rect(x, y, slot.OverlayRect.Width, slot.OverlayRect.Height);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ApplySettingsFromControls();
        ClampSlotsToCanvas();
        _placementPreviewWindow?.Close();
        base.OnClosing(e);
    }

    private static double ReadPositiveDouble(string text, double fallback) =>
        double.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static int ReadPositiveInt(string text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static double ReadDouble(string text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;

    private static int CoerceFps(int fps) =>
        FpsOptions.OrderBy(option => Math.Abs(option - fps)).First();

    private sealed record PreviewGeometry(double Left, double Top, double Width, double Height, double Scale);
}
