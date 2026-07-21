using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class OverlayRuntimeController : IDisposable
{
    private const int StopHotkeyId = 0x3141;
    private const int CustomTimerHotkeyBaseId = 0x3200;
    private readonly CaptureSessionCoordinator _captureSession;
    private readonly CpuCompositedOverlayRenderer _cpuRenderer = new();
    private readonly AppLog _log;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Stopwatch _cpuRenderClock = new();
    private OverlayRuntimeOptions? _options;
    private OverlayWindow? _overlayWindow;
    private GpuLiveOverlayService? _gpuRenderer;
    private HotkeyService? _hotkey;
    private OverlayRenderMode _activeRenderMode = OverlayRenderMode.CpuWpf;
    private long _cpuStatsLastLogTicks;
    private long _cpuStatsTicks;
    private long _cpuStatsMaxTicks;
    private int _cpuStatsFrames;
    private int _cpuStatsSkippedBusy;
    private int _cpuStatsErrors;
    private bool _isRefreshing;
    private bool _isDisposed;

    public OverlayRuntimeController(CaptureSessionCoordinator captureSession, AppLog log)
    {
        _captureSession = captureSession;
        _log = log;
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    public event Action? StopRequested;

    public event Action<Exception>? RuntimeFailed;

    public event Action<int>? CustomTimerStartRequested;

    public event Action<int>? CustomTimerCancelRequested;

    public bool IsRunning => _overlayWindow is not null;

    public async Task<OverlayRuntimeStartResult> StartAsync(Window owner, OverlayRuntimeOptions options)
    {
        ThrowIfDisposed();
        if (IsRunning)
        {
            return OverlayRuntimeStartResult.AlreadyRunning;
        }

        var hasSlotOverlay = options.Slots.Any(slot => slot.Kind == OverlayElementKind.Quickslot);
        var hasBuffMonitoring = options.BuffMonitorEnabled && options.HasSelectedBuffs;
        var hasTuairimMonitoring = options.TuairimMonitorEnabled;
        var hasCustomTimers = options.CustomTimers.Any(timer => timer.Enabled);
        if (!hasSlotOverlay && !hasBuffMonitoring && !hasTuairimMonitoring && !hasCustomTimers)
        {
            return new(OverlayRuntimeStartStatus.NoRenderableElements, null, null, null);
        }

        var requiresLiveCapture = hasSlotOverlay ||
                                  ((hasBuffMonitoring || hasTuairimMonitoring) && !options.MonitorTestMode);
        if (requiresLiveCapture && !_captureSession.HasLiveCaptureSource(options.CaptureBackend))
        {
            return new(OverlayRuntimeStartStatus.MissingCaptureSource, null, null, null);
        }

        if (!HotkeyParser.TryParse(options.Layout.StopHotkey, out var hotkeyDefinition))
        {
            return new(OverlayRuntimeStartStatus.InvalidHotkey, null, null, null);
        }

        if (requiresLiveCapture && options.CaptureBackend == CaptureBackend.Wgc)
        {
            await _captureSession.EnsureBorderlessAccessAsync();
        }

        try
        {
            Stop();
            _options = options;
            var captureTarget = _captureSession.SelectedWindow;
            _log.Info(
                $"Hotkey runtime target: title={captureTarget?.Title ?? "none"}, " +
                $"executable={captureTarget?.ProcessExecutableName ?? "none"}, " +
                $"hwnd={(captureTarget is null ? "none" : $"0x{captureTarget.Handle.ToInt64():X}")}, " +
                $"targetElevated={(captureTarget is null ? "unknown(no-target)" : ProcessPrivilegeInspector.DescribeWindowElevation(captureTarget.Handle))}.");
            _hotkey = new HotkeyService(_log);
            if (!_hotkey.Register(
                    new WindowInteropHelper(owner).Handle,
                    StopHotkeyId,
                    hotkeyDefinition.Modifiers,
                    hotkeyDefinition.VirtualKey,
                    HandleStopHotkey,
                    $"overlay.stop:{hotkeyDefinition.DisplayText}"))
            {
                _log.Info($"Stop hotkey registration failed: {hotkeyDefinition.DisplayText}");
                Stop();
                return new(OverlayRuntimeStartStatus.HotkeyRegistrationFailed, null, null, null);
            }

            var hotkeyError = RegisterCustomTimerHotkeys(owner, options.CustomTimers, hotkeyDefinition);
            if (hotkeyError is not null)
            {
                Stop();
                return hotkeyError;
            }

            _log.Info($"Stop hotkey registered: {hotkeyDefinition.DisplayText}");
            var layout = options.Layout;
            _overlayWindow = new OverlayWindow(layout.CanvasWidth, layout.CanvasHeight, layout.Opacity, options.Slots)
            {
                Left = layout.ScreenLeft,
                Top = layout.ScreenTop
            };
            _overlayWindow.Show();
            _overlayWindow.UpdateLayout();

            if (!IsOverlayWindowConfigured(_overlayWindow, out var clickThroughDetail))
            {
                var exception = _overlayWindow.ClickThroughConfigurationException
                                ?? new InvalidOperationException(clickThroughDetail);
                _log.Error("Overlay click-through configuration failed.", exception);
                Stop();
                return new(
                    OverlayRuntimeStartStatus.ClickThroughConfigurationFailed,
                    null,
                    null,
                    clickThroughDetail);
            }

            _refreshTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(layout.RefreshFps));
            _activeRenderMode = options.RequestedRenderMode;
            ResetCpuRenderStats();
            var rendererMode = RenderModeLabel(_activeRenderMode);
            if (!hasSlotOverlay)
            {
                rendererMode = "monitor.internal.overlay";
            }
            else if (_activeRenderMode == OverlayRenderMode.GpuDxgi &&
                     options.CaptureBackend == CaptureBackend.Wgc &&
                     _captureSession.WgcSelection is not null)
            {
                rendererMode = StartGpuRendererOrFallback(options);
            }
            else if (_activeRenderMode == OverlayRenderMode.GpuDxgi)
            {
                _activeRenderMode = OverlayRenderMode.CpuComposited;
                rendererMode = $"{RenderModeLabel(OverlayRenderMode.CpuComposited)} fallback";
                _log.Info(
                    $"GPU/DXGI renderer requested with captureBackend={options.CaptureBackend}. " +
                    "Falling back to CPU/Composited renderer.");
            }
            else if (requiresLiveCapture && options.CaptureBackend == CaptureBackend.Wgc)
            {
                _captureSession.StartLiveWgcCapture(options.Layout.RefreshFps);
            }

            if ((hasBuffMonitoring || hasTuairimMonitoring) &&
                !options.MonitorTestMode &&
                options.CaptureBackend == CaptureBackend.Wgc &&
                (_gpuRenderer is not null || !hasSlotOverlay))
            {
                _captureSession.StartLiveWgcCapture(maxFps: 2);
            }

            if (hasSlotOverlay)
            {
                _refreshTimer.Start();
            }

            var clickThroughStatus = _overlayWindow.IsClickThroughConfigured
                ? "click-through"
                : "not click-through";
            LogStarted(options, rendererMode);
            return new(OverlayRuntimeStartStatus.Success, rendererMode, clickThroughStatus, null);
        }
        catch (Exception exception)
        {
            _log.Error("Overlay start failed.", exception);
            Stop();
            return new(OverlayRuntimeStartStatus.Failed, null, null, exception.Message);
        }
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        LogCpuRenderStats(final: true);
        _gpuRenderer?.Dispose();
        _gpuRenderer = null;
        _captureSession.StopLiveWgcCapture();
        _overlayWindow?.Close();
        _overlayWindow = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _options = null;
        _isRefreshing = false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
    }

    public static int RefreshIntervalFromFps(int fps)
    {
        var supported = new[] { 30, 60, 120, 144 };
        var coerced = supported.OrderBy(option => Math.Abs(option - fps)).First();
        return (int)Math.Max(1, Math.Round(1000.0 / coerced));
    }

    public static string RenderModeLabel(OverlayRenderMode mode) => mode switch
    {
        OverlayRenderMode.GpuDxgi => "GPU/DXGI",
        OverlayRenderMode.CpuComposited => "CPU/Composited",
        _ => "CPU/WPF"
    };

    private string StartGpuRendererOrFallback(OverlayRuntimeOptions options)
    {
        try
        {
            var layout = options.Layout;
            _gpuRenderer = new GpuLiveOverlayService(
                new WindowInteropHelper(_overlayWindow!).Handle,
                (int)Math.Ceiling(layout.CanvasWidth),
                (int)Math.Ceiling(layout.CanvasHeight),
                _captureSession.WgcSelection!.Item,
                options.Slots,
                layout.Opacity,
                layout.RefreshFps,
                _captureSession.IsBorderlessCaptureAllowed,
                _log);
            _overlayWindow!.RenderSlots(Array.Empty<OverlaySlot>());
            _gpuRenderer.Start();
            return RenderModeLabel(OverlayRenderMode.GpuDxgi);
        }
        catch (Exception exception)
        {
            _gpuRenderer?.Dispose();
            _gpuRenderer = null;
            _log.Error(
                "GPU live overlay renderer initialization failed. Falling back to CPU/Composited renderer.",
                exception);
            _activeRenderMode = OverlayRenderMode.CpuComposited;
            _captureSession.StartLiveWgcCapture(options.Layout.RefreshFps);
            return $"{RenderModeLabel(OverlayRenderMode.CpuComposited)} fallback";
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        var options = _options;
        if (options is null ||
            !_captureSession.HasLiveCaptureSource(options.CaptureBackend) ||
            _overlayWindow is null ||
            options.Slots.Count == 0 ||
            _isRefreshing)
        {
            if (_isRefreshing)
            {
                _cpuStatsSkippedBusy++;
            }

            return;
        }

        try
        {
            _isRefreshing = true;
            _cpuRenderClock.Restart();
            if (_gpuRenderer is not null)
            {
                if (_gpuRenderer.LastException is not null)
                {
                    throw new InvalidOperationException(
                        "GPU live overlay renderer failed.",
                        _gpuRenderer.LastException);
                }

                return;
            }

            var frame = GetLiveFrame(options.CaptureBackend);
            if (frame is null)
            {
                return;
            }

            RenderCpuFrame(options, frame);
            RecordCpuRenderFrame(_cpuRenderClock.ElapsedTicks);
        }
        catch (Exception exception)
        {
            _cpuStatsErrors++;
            _log.Error("Live overlay refresh failed.", exception);
            Stop();
            RuntimeFailed?.Invoke(exception);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private BitmapSource? GetLiveFrame(CaptureBackend backend)
    {
        if (backend != CaptureBackend.Wgc)
        {
            return _captureSession.CaptureCurrentFrame(backend)
                   ?? throw new InvalidOperationException("The selected capture source is unavailable.");
        }

        if (_captureSession.LastLiveCaptureException is not null)
        {
            throw new InvalidOperationException(
                "Live WGC capture failed.",
                _captureSession.LastLiveCaptureException);
        }

        return _captureSession.TryGetLatestWgcFrame(out var frame) ? frame : null;
    }

    private void RenderCpuFrame(OverlayRuntimeOptions options, BitmapSource frame)
    {
        if (_activeRenderMode == OverlayRenderMode.CpuComposited)
        {
            var composited = _cpuRenderer.Render(
                frame,
                options.Slots,
                (int)Math.Ceiling(options.Layout.CanvasWidth),
                (int)Math.Ceiling(options.Layout.CanvasHeight),
                options.Layout.Opacity);
            _overlayWindow!.RenderCompositedFrame(composited);
            return;
        }

        foreach (var slot in options.Slots.Where(slot => slot.Kind == OverlayElementKind.Quickslot))
        {
            slot.Preview = _captureSession.Crop(frame, slot.Source.SourceRect);
        }

        _overlayWindow!.RenderSlots(options.Slots);
    }

    private void HandleStopHotkey()
    {
        Stop();
        StopRequested?.Invoke();
    }

    private OverlayRuntimeStartResult? RegisterCustomTimerHotkeys(
        Window owner,
        IReadOnlyList<CustomTimerDefinition> timers,
        HotkeyDefinition stopHotkey)
    {
        var registeredKeys = new HashSet<(uint Modifiers, uint VirtualKey)>
        {
            (stopHotkey.Modifiers, stopHotkey.VirtualKey)
        };
        var index = 0;
        foreach (var timer in timers.Where(timer => timer.Enabled))
        {
            if (!HotkeyParser.TryParse(timer.StartHotkey, out var start) ||
                !HotkeyParser.TryParse(timer.CancelHotkey, out var cancel))
            {
                return new(
                    OverlayRuntimeStartStatus.InvalidCustomTimerHotkey,
                    null,
                    null,
                    timer.Name);
            }

            if (!registeredKeys.Add((start.Modifiers, start.VirtualKey)) ||
                !registeredKeys.Add((cancel.Modifiers, cancel.VirtualKey)))
            {
                return new(
                    OverlayRuntimeStartStatus.DuplicateCustomTimerHotkey,
                    null,
                    null,
                    timer.Name);
            }

            var startId = CustomTimerHotkeyBaseId + index * 2;
            var cancelId = startId + 1;
            var handle = new WindowInteropHelper(owner).Handle;
            if (!_hotkey!.Register(
                    handle,
                    startId,
                    start.Modifiers,
                    start.VirtualKey,
                    () => CustomTimerStartRequested?.Invoke(timer.Id),
                    $"custom.timer.start:id={timer.Id},key={start.DisplayText}") ||
                !_hotkey.Register(
                    handle,
                    cancelId,
                    cancel.Modifiers,
                    cancel.VirtualKey,
                    () => CustomTimerCancelRequested?.Invoke(timer.Id),
                    $"custom.timer.cancel:id={timer.Id},key={cancel.DisplayText}"))
            {
                return new(
                    OverlayRuntimeStartStatus.CustomTimerHotkeyRegistrationFailed,
                    null,
                    null,
                    timer.Name);
            }
            index++;
        }

        return null;
    }

    private void ResetCpuRenderStats()
    {
        _cpuRenderClock.Reset();
        _cpuStatsLastLogTicks = Stopwatch.GetTimestamp();
        _cpuStatsTicks = 0;
        _cpuStatsMaxTicks = 0;
        _cpuStatsFrames = 0;
        _cpuStatsSkippedBusy = 0;
        _cpuStatsErrors = 0;
    }

    private void RecordCpuRenderFrame(long elapsedTicks)
    {
        if (_activeRenderMode == OverlayRenderMode.GpuDxgi)
        {
            return;
        }

        _cpuStatsFrames++;
        _cpuStatsTicks += elapsedTicks;
        _cpuStatsMaxTicks = Math.Max(_cpuStatsMaxTicks, elapsedTicks);
        var now = Stopwatch.GetTimestamp();
        if ((now - _cpuStatsLastLogTicks) / (double)Stopwatch.Frequency >= 5)
        {
            LogCpuRenderStats(final: false);
            _cpuStatsLastLogTicks = now;
        }
    }

    private void LogCpuRenderStats(bool final)
    {
        if (_activeRenderMode == OverlayRenderMode.GpuDxgi || _cpuStatsFrames == 0)
        {
            return;
        }

        var averageMs = _cpuStatsTicks * 1000.0 / Stopwatch.Frequency / _cpuStatsFrames;
        var maxMs = _cpuStatsMaxTicks * 1000.0 / Stopwatch.Frequency;
        _log.Info(
            $"CPU renderer stats{(final ? " final" : string.Empty)}: " +
            $"mode={RenderModeLabel(_activeRenderMode)}, frames={_cpuStatsFrames}, " +
            $"avgMs={averageMs:0.00}, maxMs={maxMs:0.00}, " +
            $"skippedBusy={_cpuStatsSkippedBusy}, errors={_cpuStatsErrors}, " +
            $"slots={_options?.Slots.Count ?? 0}");
    }

    private void LogStarted(OverlayRuntimeOptions options, string rendererMode)
    {
        var window = _overlayWindow!;
        _log.Info(
            $"Overlay started: size={options.Layout.CanvasWidth}x{options.Layout.CanvasHeight}, " +
            $"left={window.Left}, top={window.Top}, opacity={options.Layout.Opacity}, " +
            $"slots={options.Slots.Count}, hotkey={options.Layout.StopHotkey}, " +
            $"refreshFps={options.Layout.RefreshFps}, captureBackend={options.CaptureBackend}, " +
            $"renderer={rendererMode}, refreshMs={_refreshTimer.Interval.TotalMilliseconds}, " +
            $"logPath={_log.LogPath}, exStyle=0x{window.AppliedExtendedStyle.ToInt64():X16}, " +
            $"clickThrough={window.IsClickThroughConfigured}, noActivate={window.IsNoActivateConfigured}, " +
            $"topmost={window.IsTopmostConfigured}, inputHook={window.IsInputHookConfigured}");
    }

    private static bool IsOverlayWindowConfigured(OverlayWindow window, out string detail)
    {
        detail = window.ClickThroughConfigurationException?.Message ??
                 $"exStyle=0x{window.AppliedExtendedStyle.ToInt64():X16}, " +
                 $"clickThrough={window.IsClickThroughConfigured}, " +
                 $"noActivate={window.IsNoActivateConfigured}, " +
                 $"topmost={window.IsTopmostConfigured}, " +
                 $"inputHook={window.IsInputHookConfigured}";
        return window.ClickThroughConfigurationException is null &&
               window.IsClickThroughConfigured &&
               window.IsNoActivateConfigured &&
               window.IsTopmostConfigured &&
               window.IsInputHookConfigured;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

public sealed record OverlayRuntimeOptions(
    IReadOnlyList<OverlaySlot> Slots,
    OverlayLayoutSettings Layout,
    CaptureBackend CaptureBackend,
    OverlayRenderMode RequestedRenderMode,
    bool BuffMonitorEnabled,
    bool TuairimMonitorEnabled,
    bool HasSelectedBuffs,
    bool MonitorTestMode,
    IReadOnlyList<CustomTimerDefinition> CustomTimers);

public enum OverlayRuntimeStartStatus
{
    Success,
    AlreadyRunning,
    NoRenderableElements,
    MissingCaptureSource,
    InvalidHotkey,
    HotkeyRegistrationFailed,
    InvalidCustomTimerHotkey,
    DuplicateCustomTimerHotkey,
    CustomTimerHotkeyRegistrationFailed,
    ClickThroughConfigurationFailed,
    Failed
}

public sealed record OverlayRuntimeStartResult(
    OverlayRuntimeStartStatus Status,
    string? RendererMode,
    string? ClickThroughStatus,
    string? Detail)
{
    public static OverlayRuntimeStartResult AlreadyRunning { get; } =
        new(OverlayRuntimeStartStatus.AlreadyRunning, null, null, null);
}
