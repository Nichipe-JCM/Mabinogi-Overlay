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
    private readonly HashSet<OverlaySlot> _selectedSlots = new();
    private readonly Dictionary<OverlaySlot, Rect> _slotDragOrigins = new();
    private readonly Dictionary<OverlaySlot, double> _sourceSizes = new();
    private static readonly int[] FpsOptions = [30, 60, 120, 144];
    private readonly Rectangle _overlayPreviewRect = new();
    private Image? _draggingImage;
    private Rectangle? _selectionRect;
    private OverlayPlacementPreviewWindow? _placementPreviewWindow;
    private bool _isDraggingOverlayPreview;
    private bool _isSelectingSlots;
    private Point _dragOffset;
    private Point _slotDragStartPosition;
    private Point _selectionStartPosition;

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
        GridSizeSlider.ValueChanged += (_, _) => UpdateGridSizeText();
        PopulateControls();
        UpdateGridSizeText();
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

        UpdateSlotSelectionVisuals();
    }

    private void SlotImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var image = (Image)sender;
        if (!_images.TryGetValue(image, out var slot))
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!_selectedSlots.Add(slot))
            {
                _selectedSlots.Remove(slot);
            }
        }
        else if (!_selectedSlots.Contains(slot))
        {
            _selectedSlots.Clear();
            _selectedSlots.Add(slot);
        }

        UpdateSlotSelectionVisuals();
        _draggingImage = image;
        _dragOffset = e.GetPosition(_draggingImage);
        _slotDragStartPosition = e.GetPosition(EditorCanvas);
        _slotDragOrigins.Clear();
        foreach (var selectedSlot in _selectedSlots)
        {
            _slotDragOrigins[selectedSlot] = selectedSlot.OverlayRect;
        }

        _draggingImage.CaptureMouse();
        EditorCanvas.Focus();
        e.Handled = true;
    }

    private void EditorCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source != EditorCanvas)
        {
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selectedSlots.Clear();
            UpdateSlotSelectionVisuals();
        }

        _isSelectingSlots = true;
        _selectionStartPosition = e.GetPosition(EditorCanvas);
        _selectionRect = new Rectangle
        {
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(35, 0, 191, 255)),
            IsHitTestVisible = false
        };
        EditorCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, _selectionStartPosition.X);
        Canvas.SetTop(_selectionRect, _selectionStartPosition.Y);
        EditorCanvas.CaptureMouse();
        EditorCanvas.Focus();
    }

    private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isSelectingSlots && _selectionRect is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSlotSelectionRect(e.GetPosition(EditorCanvas));
            return;
        }

        if (_draggingImage is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        MoveSelectedSlotsByPointer(e.GetPosition(EditorCanvas));
    }

    private void EditorCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectingSlots)
        {
            EndSlotSelection();
            return;
        }

        _draggingImage?.ReleaseMouseCapture();
        _draggingImage = null;
        _slotDragOrigins.Clear();
        SnapSelectedSlots();
        RenderSlots();
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

    private void MoveSelectedSlotsByPointer(Point position)
    {
        if (_slotDragOrigins.Count == 0)
        {
            return;
        }

        var requestedDeltaX = position.X - _slotDragStartPosition.X;
        var requestedDeltaY = position.Y - _slotDragStartPosition.Y;
        var minDeltaX = _slotDragOrigins.Max(item => -item.Value.X);
        var minDeltaY = _slotDragOrigins.Max(item => -item.Value.Y);
        var maxDeltaX = _slotDragOrigins.Min(item => EditorCanvas.Width - item.Value.Right);
        var maxDeltaY = _slotDragOrigins.Min(item => EditorCanvas.Height - item.Value.Bottom);
        var deltaX = Math.Clamp(requestedDeltaX, minDeltaX, maxDeltaX);
        var deltaY = Math.Clamp(requestedDeltaY, minDeltaY, maxDeltaY);

        foreach (var (slot, origin) in _slotDragOrigins)
        {
            MoveSlot(slot, origin.X + deltaX, origin.Y + deltaY, snap: true);
        }
    }

    private void MoveSlot(OverlaySlot slot, double x, double y, bool snap)
    {
        var targetX = snap ? Snap(x) : x;
        var targetY = snap ? Snap(y) : y;
        targetX = Math.Clamp(targetX, 0, Math.Max(0, EditorCanvas.Width - slot.OverlayRect.Width));
        targetY = Math.Clamp(targetY, 0, Math.Max(0, EditorCanvas.Height - slot.OverlayRect.Height));
        slot.OverlayRect = new Rect(targetX, targetY, slot.OverlayRect.Width, slot.OverlayRect.Height);

        var image = _images.FirstOrDefault(pair => ReferenceEquals(pair.Value, slot)).Key;
        if (image is not null)
        {
            Canvas.SetLeft(image, targetX);
            Canvas.SetTop(image, targetY);
        }
    }

    private void UpdateSlotSelectionRect(Point position)
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

    private void EndSlotSelection()
    {
        if (_selectionRect is not null)
        {
            var selection = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            foreach (var slot in _slots)
            {
                if (selection.IntersectsWith(slot.OverlayRect))
                {
                    _selectedSlots.Add(slot);
                }
            }

            EditorCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;
        }

        EditorCanvas.ReleaseMouseCapture();
        _isSelectingSlots = false;
        UpdateSlotSelectionVisuals();
    }

    private void SnapSelectedSlots()
    {
        foreach (var slot in _selectedSlots)
        {
            MoveSlot(slot, slot.OverlayRect.X, slot.OverlayRect.Y, snap: true);
        }
    }

    private void UpdateSlotSelectionVisuals()
    {
        foreach (var (image, slot) in _images)
        {
            image.Opacity = _selectedSlots.Contains(slot) ? 1.0 : 0.72;
            image.Effect = _selectedSlots.Contains(slot)
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.DeepSkyBlue,
                    ShadowDepth = 0,
                    BlurRadius = 10,
                    Opacity = 0.95
                }
                : null;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left => new Vector(-ReadGridSize(), 0),
            Key.Right => new Vector(ReadGridSize(), 0),
            Key.Up => new Vector(0, -ReadGridSize()),
            Key.Down => new Vector(0, ReadGridSize()),
            _ => default
        };
        if (delta == default || _selectedSlots.Count == 0 || e.OriginalSource is TextBox)
        {
            return;
        }

        foreach (var slot in _selectedSlots)
        {
            MoveSlot(slot, slot.OverlayRect.X + delta.X, slot.OverlayRect.Y + delta.Y, snap: true);
        }

        e.Handled = true;
        RenderSlots();
    }

    private double Snap(double value)
    {
        var gridSize = ReadGridSize();
        return gridSize <= 1 ? value : Math.Round(value / gridSize) * gridSize;
    }

    private double ReadGridSize() => Math.Clamp(GridSizeSlider.Value, 1, 64);

    private void UpdateGridSizeText()
    {
        if (GridSizeText is not null)
        {
            GridSizeText.Text = $"Grid snap {ReadGridSize():0}px";
        }

        UpdateEditorGridBrush();
    }

    private void UpdateEditorGridBrush()
    {
        if (EditorCanvas is null)
        {
            return;
        }

        var gridSize = ReadGridSize();
        var drawingGroup = new DrawingGroup();
        using (var context = drawingGroup.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 38, 51)), null, new Rect(0, 0, gridSize, gridSize));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 148, 163, 184)), 1);
            context.DrawLine(pen, new Point(0, 0), new Point(gridSize, 0));
            context.DrawLine(pen, new Point(0, 0), new Point(0, gridSize));
        }

        EditorCanvas.Background = new DrawingBrush(drawingGroup)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, gridSize, gridSize),
            ViewportUnits = BrushMappingMode.Absolute
        };
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
