using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class MainViewportLayoutTests
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    [InlineData(900, 600)]
    public void ChangingSoundRowCountKeepsWorkspaceAndPreviewSize(double width, double height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyLayout(width, height); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyLayout(double width, double height)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        using var stream = typeof(MainViewportLayoutTests).Assembly.GetManifestResourceStream("MainWindow.Layout.xaml")!;
        var source = XDocument.Load(stream);
        var viewerSource = source.Root!.Element(wpf + "ScrollViewer")!;
        var gridSource = viewerSource.Element(wpf + "Grid")!;
        // Exercise the shipping viewport attributes/bindings without opening the app,
        // touching profiles, or starting capture/update services.
        var gridXml = new XElement(wpf + "Grid", gridSource.Attributes().Where(a => a.Name.LocalName != "Style"));
        var viewerXml = new XElement(wpf + "ScrollViewer", new XAttribute(XNamespace.Xmlns + "x", x),
            viewerSource.Attributes(), gridXml);
        var viewer = (ScrollViewer)XamlReader.Parse(viewerXml.ToString());
        var workspace = (Grid)viewer.Content;
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(260) });
        workspace.RowDefinitions.Add(new RowDefinition());
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = new Border();
        Grid.SetRow(preview, 1);
        workspace.Children.Add(preview);

        var alerts = new Grid();
        Grid.SetRow(alerts, 1);
        Grid.SetColumn(alerts, 1);
        workspace.Children.Add(alerts);
        alerts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
        alerts.RowDefinitions.Add(new RowDefinition());
        alerts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220) });
        var sounds = new StackPanel();
        var soundViewer = new ScrollViewer { Content = sounds, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(soundViewer, 1);
        alerts.Children.Add(soundViewer);
        var tuairim = new Border();
        Grid.SetRow(tuairim, 2);
        alerts.Children.Add(tuairim);
        sounds.Children.Add(new Border { Height = 40 });
        void Layout()
        {
            // Viewport bindings settle after the first ScrollContentPresenter measure.
            for (var i = 0; i < 5; i++)
            {
                viewer.Measure(new Size(width, height));
                viewer.Arrange(new Rect(0, 0, width, height));
                viewer.UpdateLayout();
            }
        }
        Layout();
        var originalWorkspace = workspace.RenderSize;
        var originalPreview = preview.RenderSize;
        var originalTuairim = tuairim.TranslatePoint(new Point(), workspace);
        var originalExtent = viewer.ExtentHeight;
        for (var i = 1; i < 8; i++) sounds.Children.Add(new Border { Height = 40 });
        Layout();
        Assert.Equal(originalWorkspace, workspace.RenderSize);
        Assert.Equal(originalPreview, preview.RenderSize);
        Assert.Equal(originalTuairim, tuairim.TranslatePoint(new Point(), workspace));
        Assert.Equal(originalExtent, viewer.ExtentHeight);
        Assert.Equal(soundViewer.ViewportHeight < 320, soundViewer.ScrollableHeight > 0);
        if (height >= 900) Assert.Equal(0, viewer.ScrollableHeight);
        else Assert.True(viewer.ScrollableHeight > 0);
        sounds.Children.RemoveRange(1, 7);
        Layout();
        Assert.Equal(originalWorkspace, workspace.RenderSize);
        Assert.Equal(0, soundViewer.ScrollableHeight);
    }
}
