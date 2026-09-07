using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using Windows.Graphics.Capture;

namespace TestOverlay.App.Services;

public sealed class CaptureSessionCoordinator
{
    private static readonly TimeSpan SingleCaptureTimeout = TimeSpan.FromSeconds(3);
    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _windowCapture = new();
    private readonly DxgiDesktopDuplicationCaptureService _dxgiCapture = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly WgcCaptureService _wgcCapture;
    private readonly AppLog _log;
    private Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object>? _captureItemClosedHandler;
    private GraphicsCaptureItem? _subscribedCaptureItem;
    private Exception? _captureSourceException;
    private int _captureSourceGeneration;
    private int _captureRequestGeneration;
    private readonly CaptureWorkQueue _desktopWork = new();
    private long _captureSamples;
    private double _captureMilliseconds;
    private long _captureAllocatedBytes;

    public CaptureSessionCoordinator(AppLog log)
    {
        _log = log;
        _wgcCapture = new WgcCaptureService(log);
    }

    public BitmapSource? CapturedImage { get; private set; }

    public GameWindowInfo? SelectedWindow { get; private set; }

    public WgcSelectionResult? WgcSelection { get; private set; }

    public bool IsWgcSupported => _wgcSupport.IsSupported();

    public bool IsBorderlessCaptureAllowed => _wgcCapture.IsBorderlessCaptureAllowed;

    public Exception? LastLiveCaptureException => _captureSourceException ?? _wgcCapture.LastLiveCaptureException;

    public IReadOnlyList<GameWindowInfo> GetVisibleWindows() => _windowDiscovery.GetVisibleWindows();

    public async Task<CaptureOperationResult> CaptureAutoAsync()
    {
        var windows = GetVisibleWindows();
        var window = SelectAutoWindow(windows);
        if (window is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.WindowNotFound, windows, null, null);
        }

        _log.Info($"Auto WGC target verified: title={window.Title}, executable={window.ProcessExecutableName}, hwnd=0x{window.Handle:X}.");
        // Requesting borderless access can yield to Windows UI. Do it before creating the
        // GraphicsCaptureItem so the item and capture session are created in one apartment.
        await _wgcCapture.EnsureBorderlessAccessAsync();
        var selection = _wgcWindowSelection.CreateForWindow(window);
        if (selection is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.WgcUnavailable, windows, window, null);
        }

        var image = await _wgcCapture.CapturePreparedItemOnceAsync(selection.Item, SingleCaptureTimeout);
        SetWgcCaptureSource(window, selection, image);
        return new CaptureOperationResult(CaptureOperationStatus.Success, windows, window, selection);
    }

    public async Task<CaptureOperationResult> CaptureManualAsync(Window owner)
    {
        var pickedSelection = await _wgcWindowSelection.PickWindowAsync(owner);
        if (pickedSelection is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.Canceled, [], null, null);
        }

        _log.Info($"Manual WGC picker result: name={pickedSelection.DisplayName}, size={pickedSelection.Width}x{pickedSelection.Height}.");
        var windows = GetVisibleWindows();
        var window = MatchPickedWindow(windows, pickedSelection.DisplayName);
        if (window is null)
        {
            _log.Info($"Manual WGC target verification failed: pickerName={pickedSelection.DisplayName}, visibleWindows={windows.Count}.");
            return new CaptureOperationResult(CaptureOperationStatus.NotMabinogi, windows, null, pickedSelection);
        }

        _log.Info($"Manual WGC target verified: pickerName={pickedSelection.DisplayName}, title={window.Title}, executable={window.ProcessExecutableName}, hwnd=0x{window.Handle:X}.");
        // Never capture the picker item directly. The picker can return a monitor item such
        // as "Display 1" even when its preview looks like the game. Recreate the item from
        // the verified Client.exe HWND so manual capture cannot silently become screen capture.
        await _wgcCapture.EnsureBorderlessAccessAsync();
        var selection = _wgcWindowSelection.CreateForWindow(window);
        if (selection is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.WgcUnavailable, windows, window, null);
        }

        var image = await _wgcCapture.CapturePreparedItemOnceAsync(selection.Item, SingleCaptureTimeout);
        SetWgcCaptureSource(window, selection, image);
        return new CaptureOperationResult(CaptureOperationStatus.Success, windows, window, selection);
    }

    public BitmapSource Crop(BitmapSource source, Rect rect) => _windowCapture.Crop(source, rect);

    public async Task<BitmapSource?> CaptureCurrentFrameAsync(CaptureBackend backend, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestGeneration = Volatile.Read(ref _captureRequestGeneration);
        var generation = Volatile.Read(ref _captureSourceGeneration);
        if (requestGeneration != Volatile.Read(ref _captureRequestGeneration) ||
            generation != Volatile.Read(ref _captureSourceGeneration)) return null;
        if (LastLiveCaptureException is { } failure)
            throw new InvalidOperationException("The selected capture source is unavailable.", failure);
        if (backend == CaptureBackend.Wgc) return CaptureCurrentFrame(backend);
        var window = SelectedWindow;
        if (window is null) return null;
        var frame = await _desktopWork.RunAsync(() => backend == CaptureBackend.DxgiDesktopDuplication
            ? _dxgiCapture.CaptureClientArea(window)
            : _windowCapture.CaptureClientArea(window), (elapsed, allocated) =>
            {
                _captureMilliseconds += elapsed;
                _captureAllocatedBytes += allocated;
                if (++_captureSamples % 120 == 0)
                    _log.Info($"Desktop capture metrics: backend={backend}, samples={_captureSamples}, " +
                        $"avgMs={_captureMilliseconds / _captureSamples:0.000}, managedBytesPerFrame={_captureAllocatedBytes / _captureSamples}, dxgiSessions={_dxgiCapture.SessionCreationCount}");
            }, cancellationToken);
        return generation == Volatile.Read(ref _captureSourceGeneration) &&
            requestGeneration == Volatile.Read(ref _captureRequestGeneration) ? frame : null;
    }

    public BitmapSource? CaptureCurrentFrame(CaptureBackend backend)
    {
        if (backend == CaptureBackend.Wgc)
        {
            return _wgcCapture.TryGetLatestFrame(out var frame) ? frame : null;
        }

        if (SelectedWindow is null)
        {
            return null;
        }

        return backend == CaptureBackend.DxgiDesktopDuplication
            ? _dxgiCapture.CaptureClientArea(SelectedWindow)
            : _windowCapture.CaptureClientArea(SelectedWindow);
    }

    public Task<WgcBorderlessAccessState> EnsureBorderlessAccessAsync() =>
        _wgcCapture.EnsureBorderlessAccessAsync();

    public void StartLiveWgcCapture(int maxFps = 0)
    {
        if (WgcSelection is null)
        {
            throw new InvalidOperationException("No WGC capture source is selected.");
        }

        _wgcCapture.StartLiveCapture(WgcSelection.Item, maxFps);
    }

    public void StopLiveWgcCapture()
    {
        Interlocked.Increment(ref _captureRequestGeneration);
        _wgcCapture.StopLiveCapture();
        _ = ResetDesktopCaptureAsync();
    }

    private async Task ResetDesktopCaptureAsync()
    {
        try
        {
            await _desktopWork.ResetAsync(() =>
            {
                _dxgiCapture.Reset();
                _captureSamples = 0;
                _captureMilliseconds = 0;
                _captureAllocatedBytes = 0;
            });
        }
        catch (Exception exception) { _log.Error("Desktop capture cleanup failed.", exception); }
    }

    public bool TryGetLatestWgcFrame(out BitmapSource? frame) =>
        _wgcCapture.TryGetLatestFrame(out frame);

    public bool HasLiveCaptureSource(CaptureBackend backend) =>
        backend == CaptureBackend.Wgc ? WgcSelection is not null : SelectedWindow is not null;

    public static GameWindowInfo? SelectAutoWindow(IReadOnlyList<GameWindowInfo> windows) =>
        windows.FirstOrDefault(item => item.IsPreferredMabinogiClient)
        ?? windows.FirstOrDefault(item => item.IsExactClientExecutable && item.LooksLikeMabinogi)
        ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi);

    public static GameWindowInfo? MatchPickedWindow(
        IReadOnlyList<GameWindowInfo> windows,
        string displayName)
    {
        var titleMatch = windows.FirstOrDefault(item =>
            item.IsPreferredMabinogiClient && MatchesDisplayName(item, displayName))
            ?? windows.FirstOrDefault(item =>
                item.IsExactClientExecutable && item.LooksLikeMabinogi && MatchesDisplayName(item, displayName))
            ?? windows.FirstOrDefault(item =>
                item.LooksLikeMabinogi && MatchesDisplayName(item, displayName));
        if (titleMatch is not null)
        {
            return titleMatch;
        }

        if (!IsGenericDisplayName(displayName))
        {
            return null;
        }

        // A generic monitor label carries no window identity. It is safe to recover only
        // when exactly one real Client.exe window is available; otherwise reject it.
        var exactClients = windows
            .Where(item => item.IsExactClientExecutable && item.LooksLikeMabinogi)
            .ToList();
        return exactClients.Count == 1 ? exactClients[0] : null;
    }

    public static bool IsGenericDisplayName(string displayName)
    {
        var normalized = displayName.Trim();
        return normalized.StartsWith("Display ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("Monitor ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("\uB514\uC2A4\uD50C\uB808\uC774 ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("\uBAA8\uB2C8\uD130 ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDisplayName(GameWindowInfo window, string displayName) =>
        string.Equals(window.Title, displayName, StringComparison.OrdinalIgnoreCase)
        || displayName.Contains(window.Title, StringComparison.OrdinalIgnoreCase)
        || window.Title.Contains(displayName, StringComparison.OrdinalIgnoreCase);

    private void SetWgcCaptureSource(
        GameWindowInfo window,
        WgcSelectionResult selection,
        BitmapSource image)
    {
        UnsubscribeCaptureItemClosed();
        SelectedWindow = window;
        WgcSelection = selection;
        CapturedImage = image;
        _captureSourceException = null;
        var generation = Interlocked.Increment(ref _captureSourceGeneration);
        _captureItemClosedHandler = (item, _) =>
        {
            if (generation != Volatile.Read(ref _captureSourceGeneration))
            {
                return;
            }

            _captureSourceException = new InvalidOperationException(
                "The selected game capture source was closed.");
            WgcSelection = null;
            SelectedWindow = null;
            CapturedImage = null;
            _log.Info("The selected WGC capture source was closed. Runtime reselection is required.");
        };
        _subscribedCaptureItem = selection.Item;
        selection.Item.Closed += _captureItemClosedHandler;
    }

    private void UnsubscribeCaptureItemClosed()
    {
        Interlocked.Increment(ref _captureSourceGeneration);
        if (_subscribedCaptureItem is null || _captureItemClosedHandler is null)
        {
            return;
        }

        try
        {
            _subscribedCaptureItem.Closed -= _captureItemClosedHandler;
        }
        catch (ObjectDisposedException)
        {
            // The capture item may already be closed while a new source is being selected.
        }

        _captureItemClosedHandler = null;
        _subscribedCaptureItem = null;
    }
}

public enum CaptureOperationStatus
{
    Success,
    WindowNotFound,
    WgcUnavailable,
    Canceled,
    NotMabinogi
}

public sealed record CaptureOperationResult(
    CaptureOperationStatus Status,
    IReadOnlyList<GameWindowInfo> Windows,
    GameWindowInfo? Window,
    WgcSelectionResult? Selection);
