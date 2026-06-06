using System.Windows;
using TestOverlay.App.Native;

namespace TestOverlay.App;

public partial class App : Application
{
    public App()
    {
        Win32Methods.TryEnablePerMonitorDpiAwareness();
    }
}
