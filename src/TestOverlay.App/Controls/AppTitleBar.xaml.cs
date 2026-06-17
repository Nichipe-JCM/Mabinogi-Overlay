using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TestOverlay.App.Controls;

public partial class AppTitleBar : UserControl
{
    private Window? _window;

    public AppTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += Window_StateChanged;
        UpdateMaximizeButton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }
    }

    private void TitleBarArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var window = _window ?? Window.GetWindow(this);
        if (window is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2 && CanResize(window))
        {
            ToggleMaximize(window);
            e.Handled = true;
            return;
        }

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if focus or mouse capture changes while dragging.
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_window ?? Window.GetWindow(this)) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_window ?? Window.GetWindow(this)) is { } window && CanResize(window))
        {
            ToggleMaximize(window);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        (_window ?? Window.GetWindow(this))?.Close();

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMaximizeButton();

    private void UpdateMaximizeButton()
    {
        var window = _window ?? Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        MaximizeButton.Visibility = CanResize(window) ? Visibility.Visible : Visibility.Collapsed;
        MaximizeIcon.Text = window.WindowState == WindowState.Maximized
            ? char.ConvertFromUtf32(0x1F5D7)
            : char.ConvertFromUtf32(0x1F5D6);
    }

    private static bool CanResize(Window window) =>
        window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    private static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
