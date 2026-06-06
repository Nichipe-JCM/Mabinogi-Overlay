using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class LayoutEditorWindow : Window
{
    private readonly IReadOnlyList<OverlaySlot> _slots;
    private readonly Dictionary<Image, OverlaySlot> _images = new();
    private readonly Dictionary<OverlaySlot, double> _sourceSizes = new();
    private Image? _draggingImage;
    private Point _dragOffset;

    public LayoutEditorWindow(
        double canvasWidth,
        double canvasHeight,
        double screenLeft,
        double screenTop,
        double opacity,
        string hotkey,
        int refreshIntervalMs,
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
        RefreshIntervalMs = Math.Clamp(refreshIntervalMs, 100, 5000);
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
        RenderSlots();
    }

    public double CanvasWidth { get; private set; }

    public double CanvasHeight { get; private set; }

    public double ScreenLeft { get; private set; }

    public double ScreenTop { get; private set; }

    public double OverlayOpacity { get; private set; }

    public string StopHotkey { get; private set; } = "Ctrl+Shift+F8";

    public int RefreshIntervalMs { get; private set; }

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
        RefreshIntervalBox.Text = RefreshIntervalMs.ToString();
        SlotScaleSlider.Value = SlotScale;
        SlotScaleText.Text = $"{SlotScale:0.0}x";
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
    }

    private void ApplySettingsFromControls()
    {
        CanvasWidth = ReadPositiveDouble(CanvasWidthBox.Text, CanvasWidth);
        CanvasHeight = ReadPositiveDouble(CanvasHeightBox.Text, CanvasHeight);
        ScreenLeft = ReadDouble(ScreenLeftBox.Text, ScreenLeft);
        ScreenTop = ReadDouble(ScreenTopBox.Text, ScreenTop);
        OverlayOpacity = Math.Clamp(OpacitySlider.Value, 0.2, 1);
        StopHotkey = HotkeyBox.Text;
        RefreshIntervalMs = Math.Clamp(ReadPositiveInt(RefreshIntervalBox.Text, RefreshIntervalMs), 100, 5000);
        SlotScale = Math.Clamp(SlotScaleSlider.Value, 1, 3);
        ApplyCanvasSize();
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
        base.OnClosing(e);
    }

    private static double ReadPositiveDouble(string text, double fallback) =>
        double.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static int ReadPositiveInt(string text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static double ReadDouble(string text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;
}
