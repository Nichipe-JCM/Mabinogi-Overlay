using System.Windows;
using TestOverlay.App.Native;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class App : Application
{
    private readonly AppLog _log = new();

    public App()
    {
        Win32Methods.TryEnablePerMonitorDpiAwareness();
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true;
        };
    }
}
