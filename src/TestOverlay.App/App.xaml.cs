using System.Windows;
using TestOverlay.App.Native;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class App : Application
{
    private readonly AppLog _log = new();

    public App()
    {
        _log.Info("Application bootstrap started.");
        Win32Methods.TryEnablePerMonitorDpiAwareness();
        if (AppDataPaths.LastMigrationException is not null)
        {
            _log.Error(
                "Portable user data migration failed. The application will continue with the LocalAppData directory.",
                AppDataPaths.LastMigrationException);
        }
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

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded));
        base.OnStartup(e);
        if (BenchmarkWindow.HasAutomationArgs(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var benchmarkWindow = new BenchmarkWindow();
            MainWindow = benchmarkWindow;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await benchmarkWindow.RunAutomationFromArgsAsync(e.Args);
                }
                finally
                {
                    Shutdown();
                }
            });
            return;
        }

        try
        {
            var mainWindow = new MainWindow(_log);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            _log.Error("Main window startup failed.", exception);
            Shutdown(-1);
        }
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            WindowCornerService.ApplyStandardCorners(window);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.Info("Application exiting.");
        _log.Dispose();
        base.OnExit(e);
    }
}
