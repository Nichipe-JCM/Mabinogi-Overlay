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
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _log.Error("Unhandled app domain exception.", exception);
            }
            else
            {
                _log.Info($"Unhandled app domain exception object: {args.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }
}
