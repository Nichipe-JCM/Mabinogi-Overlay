using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TestOverlay.App.Models;

namespace TestOverlay.App;

public partial class OverlayPlacementPreviewWindow : Window
{
    private const double ControlHeaderHeight = 34;
    private const double PreviewBorderThickness = 2;
    private const double MinimumOverlayWidth = 120;
    private const double MinimumOverlayHeight = 80;
    private readonly IReadOnlyList<OverlaySlot> _slots;
    private readonly Action<double, double, double, double> _placementChanged;
    private readonly Action<OverlaySlot> _slotDragStarted;
    private readonly Action<OverlaySlot, double, double> _slotMoved;
    private readonly Action<OverlaySlot> _slotDragCompleted;
    private double _defaultSlotOpacity;
    private double _canvasWidth;
    private double _canvasHeight;
    private bool _isPositionEditingEnabled;
    private Image? _draggingImage;
    private OverlaySlot? _draggingSlot;
    private Point _slotDragPointerOrigin;
    private Rect _slotDragOrigin;

    public OverlayPlacementPreviewWindow(
        double left,
        double top,
        double width,
        double height,
        double opacity,
        IReadOnlyList<OverlaySlot> slots,
        bool isPositionEditingEnabled,
        Action<double, double, double, double> placementChanged,
        Action<OverlaySlot> slotDragStarted,
        Action<OverlaySlot, double, double> slotMoved,
        Action<OverlaySlot> slotDragCompleted)
    {
        InitializeComponent();
        _slots = slots;
        _placementChanged = placementChanged;
        _slotDragStarted = slotDragStarted;
        _slotMoved = slotMoved;
        _slotDragCompleted = slotDragCompleted;
        _defaultSlotOpacity = Math.Clamp(opacity, 0, 1);
        _canvasWidth = Math.Max(MinimumOverlayWidth, width);
        _canvasHeight = Math.Max(MinimumOverlayHeight, height);
        _isPositionEditingEnabled = isPositionEditingEnabled;
        PreviewCanvas.IsHitTestVisible = isPositionEditingEnabled;
        Left = left - PreviewBorderThickness;
        Top = top - ControlHeaderHeight - PreviewBorderThickness;
        Width = Math.Max(MinimumOverlayWidth + (PreviewBorderThickness * 2), _canvasWidth + (PreviewBorderThickness * 2));
        Height = Math.Max(MinHeight, _canvasHeight + ControlHeaderHeight + (PreviewBorderThickness * 2));
        Opacity = 1;
        RenderSlots();
        LocationChanged += (_, _) => NotifyPlacementChanged();
        SizeChanged += (_, _) =>
        {
            RenderSlots();
        };
    }

    private void RenderSlots()
    {
        PreviewCanvas.Children.Clear();

        foreach (var slot in _slots)
        {
            var image = new Image
            {
                Source = slot.Preview,
                Width = slot.OverlayRect.Width,
                Height = slot.OverlayRect.Height,
                Stretch = Stretch.Fill,
                Opacity = slot.EffectiveOpacity(_defaultSlotOpacity) * 0.9,
                Cursor = _isPositionEditingEnabled ? Cursors.SizeAll : Cursors.Arrow,
                IsHitTestVisible = _isPositionEditingEnabled,
                Tag = slot
            };
            image.MouseLeftButtonDown += SlotImage_MouseLeftButtonDown;
            image.MouseMove += SlotImage_MouseMove;
            image.MouseLeftButtonUp += SlotImage_MouseLeftButtonUp;
            Canvas.SetLeft(image, slot.OverlayRect.X);
            Canvas.SetTop(image, slot.OverlayRect.Y);
            PreviewCanvas.Children.Add(image);
        }
    }

    public void RefreshSlots(double opacity, bool isPositionEditingEnabled)
    {
        _defaultSlotOpacity = Math.Clamp(opacity, 0, 1);
        _isPositionEditingEnabled = isPositionEditingEnabled;
        PreviewCanvas.IsHitTestVisible = isPositionEditingEnabled;
        RenderSlots();
    }

    private void SlotImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isPositionEditingEnabled || sender is not Image image)
        {
            return;
        }

        if (image.Tag is not OverlaySlot slot)
        {
            return;
        }

        _draggingImage = image;
        _draggingSlot = slot;
        _slotDragPointerOrigin = e.GetPosition(PreviewCanvas);
        _slotDragOrigin = slot.OverlayRect;
        _slotDragStarted(slot);
        image.CaptureMouse();
        e.Handled = true;
    }

    private void SlotImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingImage is null || _draggingSlot is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(PreviewCanvas);
        _slotMoved(
            _draggingSlot,
            _slotDragOrigin.X + position.X - _slotDragPointerOrigin.X,
            _slotDragOrigin.Y + position.Y - _slotDragPointerOrigin.Y);
        Canvas.SetLeft(_draggingImage, _draggingSlot.OverlayRect.X);
        Canvas.SetTop(_draggingImage, _draggingSlot.OverlayRect.Y);
        e.Handled = true;
    }

    private void SlotImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingImage is null || _draggingSlot is null)
        {
            return;
        }

        _draggingImage.ReleaseMouseCapture();
        var slot = _draggingSlot;
        _draggingImage = null;
        _draggingSlot = null;
        _slotDragCompleted(slot);
        e.Handled = true;
    }

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        NotifyPlacementChanged();
    }

    private void PreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        NotifyPlacementChanged();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinimumOverlayWidth + (PreviewBorderThickness * 2), Width + e.HorizontalChange);
        Height = Math.Max(
            MinimumOverlayHeight + ControlHeaderHeight + (PreviewBorderThickness * 2),
            Height + e.VerticalChange);
        _canvasWidth = Math.Max(MinimumOverlayWidth, Width - (PreviewBorderThickness * 2));
        _canvasHeight = Math.Max(
            MinimumOverlayHeight,
            Height - ControlHeaderHeight - (PreviewBorderThickness * 2));
        NotifyPlacementChanged();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void NotifyPlacementChanged() =>
        _placementChanged(
            Math.Round(Left + PreviewBorderThickness),
            Math.Round(Top + ControlHeaderHeight + PreviewBorderThickness),
            Math.Round(_canvasWidth),
            Math.Round(_canvasHeight));
}
