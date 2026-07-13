using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class CaptureSessionCoordinatorTests
{
    [Fact]
    public void SelectAutoWindow_PrefersExactClientWindowWithPreferredTitle()
    {
        var generic = Window("Mabinogi", "Mabinogi.exe");
        var exactClient = Window("Another client title", "Client.exe");
        var preferred = Window("마비노기 (Client)", "Client.exe");

        var selected = CaptureSessionCoordinator.SelectAutoWindow([generic, exactClient, preferred]);

        Assert.Same(preferred, selected);
    }

    [Fact]
    public void MatchPickedWindow_PrefersMatchingMabinogiTitle()
    {
        var first = Window("Mabinogi - Character A", "Client.exe");
        var second = Window("Mabinogi - Character B", "Client.exe");

        var selected = CaptureSessionCoordinator.MatchPickedWindow([first, second], "Mabinogi - Character B");

        Assert.Same(second, selected);
    }

    private static GameWindowInfo Window(string title, string executable) =>
        new(1, title, "Client", executable, 1280, 720);
}
