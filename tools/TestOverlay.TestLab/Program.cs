using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--instance-child"))
        {
            using var instance = new SingleInstanceCoordinator();
            if (!instance.TryAcquirePrimary()) { instance.SignalPrimaryInstance(); return; }
            File.WriteAllText(Path.Combine(TestLabEnvironment.Root!, "instance-ready"), "ready");
            using var activated = new ManualResetEventSlim();
            Thread.Sleep(300);
            instance.Listen(activated.Set);
            if (!activated.Wait(TimeSpan.FromSeconds(10))) Environment.Exit(2);
            return;
        }
        var root = TestLabEnvironment.Root ?? Path.Combine(AppContext.BaseDirectory, "sessions", Guid.NewGuid().ToString("N"));
        if (args.Length == 2 && args[0] == "--prepare-only") root = Path.GetFullPath(args[1]);
        Seed(root);
        if (args.Contains("--prepare-only")) return;
        if (args.Contains("--verify-sandbox")) { VerifySandbox(root); return; }
        new Application().Run(new LabWindow(root));
    }

    private static void VerifySandbox(string root)
    {
        if (!TestLabEnvironment.Enabled || AppDataPaths.RootDirectory != root) throw new InvalidOperationException("Isolation not enabled.");
        var game = new GameWindowInfo(1, "Mabinogi", "Client", "Client.exe", 800, 600);
        var target = new GameWindowInfo(2, TestLabEnvironment.TargetTitle, "TestOverlay.TestLab", "TestOverlay.TestLab.exe", 800, 600);
        if (CaptureSessionCoordinator.SelectAutoWindow([game]) is not null ||
            CaptureSessionCoordinator.SelectAutoWindow([game, target]) != target ||
            CaptureSessionCoordinator.MatchPickedWindow([game], game.Title) is not null ||
            CaptureSessionCoordinator.MatchPickedWindow([game, target], target.Title) != target)
            throw new InvalidOperationException("Test capture target isolation failed.");
        var store = new ProfileStore(Path.Combine(root, "Profiles"));
        var profile = store.Load("lab")!;
        var profileFault = Path.Combine(root, "faults", "profile-save");
        File.WriteAllText(profileFault, "on");
        try { store.Save(profile, "lab"); throw new InvalidOperationException("Profile fault did not fire."); }
        catch (IOException) { }
        File.Delete(profileFault);
        store.Save(profile, "lab");
        var erin = new ErinTimerSettingsStore();
        var settings = erin.Load();
        var erinFault = Path.Combine(root, "faults", "erin-save");
        File.WriteAllText(erinFault, "on");
        try { erin.Save(settings); throw new InvalidOperationException("Erin fault did not fire."); }
        catch (IOException) { }
        File.Delete(erinFault);
        erin.Save(settings);
        var path = store.GetProfilePath("lab");
        File.WriteAllText(path, "{broken");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (store.Load("lab") is null || !store.LastLoadRecoveredFromBackup || store.LastRestoreException is null)
                throw new InvalidOperationException("Backup recovery check failed.");
        }
        store.Save(profile, "lab");
        ProcessStartInfo Child()
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TestOverlay.TestLab.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--instance-child"); info.ArgumentList.Add("--test-lab");
            info.Environment[TestLabEnvironment.RootVariable] = root;
            return info;
        }
        using var primary = Process.Start(Child())!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Path.Combine(root, "instance-ready")) && !primary.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (!File.Exists(Path.Combine(root, "instance-ready"))) throw new InvalidOperationException("Primary did not start.");
        using var secondary = Process.Start(Child())!;
        if (!secondary.WaitForExit(12000) || !primary.WaitForExit(12000) || primary.ExitCode != 0 || secondary.ExitCode != 0)
            throw new InvalidOperationException("Cross-process activation check failed.");
        File.WriteAllText(Path.Combine(root, "verification.txt"), "PASS: test target isolation; isolated profile/Erin save faults and recovery; locked-primary backup recovery; two-process early activation. No game capture or app GUI was started.");
    }

    private static void Seed(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        Directory.CreateDirectory(Path.Combine(root, "faults"));
        var options = new JsonSerializerOptions { WriteIndented = true };
        var settings = new AppSettings { SchemaVersion = 1, ProfileDirectory = Path.Combine(root, "Profiles"),
            ActiveProfileName = "lab", AutomaticRendererSelection = false, AutomaticCaptureSelection = false,
            OverlayRenderMode = OverlayRenderMode.CpuComposited, CaptureBackend = CaptureBackend.Wgc, CloseBehavior = AppCloseBehavior.Exit };
        var profile = new OverlayProfile { Name = "lab", ScreenLeft = 80, ScreenTop = 600, CanvasHeight = 140, CanvasWidth = 420 };
        // A broad animated patch stays inside the test target across common display scales.
        profile.Candidates.Add(new OverlayProfileCandidate { Id = 1, SourceX = 0, SourceY = 0, SourceWidth = 240, SourceHeight = 120, Score = 1 });
        profile.Slots.Add(new OverlayProfileSlot { SourceCandidateId = 1, SourceWidth = 240, SourceHeight = 120, OverlayWidth = 240, OverlayHeight = 120 });
        WriteNew(Path.Combine(root, "settings.json"), settings, options);
        WriteNew(Path.Combine(root, "Profiles", "lab.json"), profile, options);
        WriteNew(Path.Combine(root, "Profiles", "lab.json.bak"), profile, options);
        WriteNew(Path.Combine(root, "erin-timer.json"), new ErinTimerSettings { DesktopNotifications = false,
            Alarms = [new ErinAlarm { Id = 1, Name = "lab", Hour = 12, Minute = 0, Enabled = false }] }, options);
    }

    private static void WriteNew<T>(string path, T value, JsonSerializerOptions options)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, value, options);
    }
}

internal sealed class LabWindow : Window
{
    private readonly string _root;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private Window? _target;
    private Process? _primary;

    public LabWindow(string root)
    {
        _root = root;
        Title = L.T("lab.control.title"); Width = 760; Height = 720;
        var panel = new StackPanel { Margin = new Thickness(12) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = L.T("lab.instructions"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = root, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Add(panel, "lab.launch", () => { ShowTarget(); if (_primary is null || _primary.HasExited) { _primary?.Dispose(); _primary = Launch(); } Show("lab.launched"); });
        Add(panel, "lab.target.open", ShowTarget);
        Add(panel, "lab.target.resize", () => { if (_target is not null) { _target.Width = _target.Width > 850 ? 700 : 950; _target.Height = _target.Height > 450 ? 380 : 520; } });
        Add(panel, "lab.target.minimize", () => { if (_target is not null) _target.WindowState = WindowState.Minimized; });
        Add(panel, "lab.target.close", () => _target?.Close());
        Toggle(panel, "lab.pause", "capture-pause");
        Toggle(panel, "lab.capture.fail", "capture-fail");
        Toggle(panel, "lab.capture.delay", "capture-delay");
        Toggle(panel, "lab.profile.fail", "profile-save");
        Toggle(panel, "lab.erin.fail", "erin-save");
        Add(panel, "lab.backup", () =>
        {
            var path = Path.Combine(_root, "Profiles", "lab.json");
            File.WriteAllText(path, "{ intentionally damaged by test lab");
            Show("lab.backup.ready");
        });
        var second = new Button { Content = L.T("lab.second"), Margin = new Thickness(2), Padding = new Thickness(8) };
        second.Click += async (_, _) =>
        {
            second.IsEnabled = false;
            try
            {
                var before = ReadActivationCount();
                if (_primary is null || _primary.HasExited) { ShowTarget(); _primary?.Dispose(); _primary = Launch(); before = 0; await Task.Delay(100); }
                using var process = Launch();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                for (var i = 0; i < 50 && ReadActivationCount() <= before; i++) await Task.Delay(100);
                Show(process.ExitCode == 0 && !_primary.HasExited && ReadActivationCount() > before ? "lab.second.pass" : "lab.second.fail");
            }
            catch (Exception exception) { _status.Text = exception.Message; }
            finally { second.IsEnabled = true; }
        };
        panel.Children.Add(second);
        panel.Children.Add(_status); panel.Children.Add(_log);
        _poll.Tick += (_, _) =>
        {
            try
            {
                var path = Path.Combine(_root, "Logs", "app.log");
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    _log.Text = string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').TakeLast(9));
                }
            }
            catch (IOException) { }
        };
        _poll.Start();
        Closed += (_, _) => { _poll.Stop(); _target?.Close(); _primary?.Dispose(); };
    }

    private int ReadActivationCount()
    {
        try { return int.TryParse(File.ReadAllText(Path.Combine(_root, "activations.txt")), out var count) ? count : 0; }
        catch (IOException) { return 0; }
    }

    private Process Launch()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Mabinogi Overlay.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--test-lab");
        start.Environment[TestLabEnvironment.RootVariable] = _root;
        return Process.Start(start) ?? throw new InvalidOperationException(L.T("lab.launch.failed"));
    }

    private void Add(Panel panel, string key, Action action)
    {
        var button = new Button { Content = L.T(key), Margin = new Thickness(2), Padding = new Thickness(8) };
        button.Click += (_, _) => { try { action(); } catch (Exception exception) { _status.Text = exception.Message; } };
        panel.Children.Add(button);
    }

    private void Toggle(Panel panel, string key, string fault)
    {
        var check = new CheckBox { Content = L.T(key), Margin = new Thickness(6) };
        check.Click += (_, _) =>
        {
            try
            {
                var path = Path.Combine(_root, "faults", fault);
                if (check.IsChecked == true) File.WriteAllText(path, "enabled"); else File.Delete(path);
                Show("lab.fault.changed");
            }
            catch (Exception exception) { _status.Text = exception.Message; }
        };
        panel.Children.Add(check);
    }

    private void Show(string key) => _status.Text = L.T(key);

    private void ShowTarget()
    {
        if (_target is not null) { _target.WindowState = WindowState.Normal; _target.Activate(); return; }
        var frame = 0;
        var clicks = 0;
        var counter = new TextBlock { Foreground = Brushes.White, FontSize = 28, Margin = new Thickness(12) };
        var targetPanel = new StackPanel { Background = Brushes.DarkSlateBlue };
        targetPanel.Children.Add(counter);
        var click = new Button { Content = L.T("lab.target.click"), Height = 60, Margin = new Thickness(10) };
        click.Click += (_, _) => clicks++;
        targetPanel.Children.Add(click);
        targetPanel.Children.Add(new TextBlock { Text = L.T("lab.target.help"), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => counter.Text = L.F("lab.target.counter", ++frame, clicks);
        _target = new Window { Title = TestLabEnvironment.TargetTitle, Width = 900, Height = 480, Left = 30, Top = 30, Content = targetPanel };
        _target.Closed += (_, _) => { timer.Stop(); _target = null; };
        _target.Show(); timer.Start();
    }
}
