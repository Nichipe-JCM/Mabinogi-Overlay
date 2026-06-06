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
    private Image? _draggingImage;
    private Point _dragOffset;

    public LayoutEditorWindow(double canvasWidth, double canvasHeight, IReadOnlyList<OverlaySlot> slots)
    {
        InitializeComponent();
        _slots = slots;
        EditorCanvas.Width = Math.Max(120, canvasWidth);
        EditorCanvas.Height = Math.Max(80, canvasHeight);
        RenderSlots();
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
}
