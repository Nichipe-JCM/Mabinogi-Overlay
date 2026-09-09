using System.Windows;
using TestOverlay.App.Native;
using TestOverlay.App.Services;
using TestOverlay.Update;

namespace TestOverlay.App;

public partial class App : Application
{
    private readonly AppLog _log = new();
    private readonly SingleInstanceCoordinator _singleInstance = new();
    private int _fatalErrorHandling;

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
            if (Interlocked.Exchange(ref _fatalErrorHandling, 1) != 0)
            {
                args.Handled = false;
                return;
            }

            var fatalLogPath = _log.WriteCritical("Unhandled UI exception.", args.Exception);
            // An unknown dispatcher exception can leave capture, native handles, or profile
            // state only partially updated. Terminate deliberately instead of pretending the
            // application recovered and allowing it to continue in an indeterminate state.
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"예기치 않은 오류로 앱을 종료합니다. 로그를 확인해 주세요.\n\n" +
                    $"The app will close because of an unexpected error. Please check the log.\n\n" +
                    (fatalLogPath is not null
                        ? fatalLogPath
                        : $"{args.Exception.GetType().Name}: {args.Exception.Message}"),
                    "Mabinogi Overlay",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // The dispatcher is already failing; continue with controlled shutdown.
            }
            Shutdown(-1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _log.WriteCritical("Unhandled app domain exception.", exception);
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded));
        base.OnStartup(e);
        var benchmarkAutomation = BenchmarkWindow.HasAutomationArgs(e.Args);
        if (!benchmarkAutomation && !_singleInstance.TryAcquirePrimary())
        {
            _log.Info("A second application instance requested activation of the primary instance.");
            _singleInstance.SignalPrimaryInstance();
            Shutdown();
            return;
        }

        if (benchmarkAutomation)
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
            using var updateStartup = await UpdateStartupGate.ConnectAsync(e.Args);
            if (updateStartup is null && UpdateTransaction.Pending(AppContext.BaseDirectory).Length != 0)
            {
                MessageBox.Show(L.T("update.recovery.required"), L.T("update.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(-1);
                return;
            }
            var mainWindow = new MainWindow(_log);
            MainWindow = mainWindow;
            mainWindow.Show();
            _singleInstance.Attach(mainWindow, mainWindow.ActivateFromSecondInstance);
            if (updateStartup is not null) await updateStartup.CompleteAsync();
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
        _singleInstance.Dispose();
        _log.Dispose();
        base.OnExit(e);
    }
}
