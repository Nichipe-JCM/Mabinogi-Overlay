using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

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

    public CaptureSessionCoordinator(AppLog log)
    {
        _wgcCapture = new WgcCaptureService(log);
    }

    public BitmapSource? CapturedImage { get; private set; }

    public GameWindowInfo? SelectedWindow { get; private set; }

    public WgcSelectionResult? WgcSelection { get; private set; }

    public bool IsWgcSupported => _wgcSupport.IsSupported();

    public bool IsBorderlessCaptureAllowed => _wgcCapture.IsBorderlessCaptureAllowed;

    public Exception? LastLiveCaptureException => _wgcCapture.LastLiveCaptureException;

    public IReadOnlyList<GameWindowInfo> GetVisibleWindows() => _windowDiscovery.GetVisibleWindows();

    public async Task<CaptureOperationResult> CaptureAutoAsync()
    {
        var windows = GetVisibleWindows();
        var window = SelectAutoWindow(windows);
        if (window is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.WindowNotFound, windows, null, null);
        }

        var selection = _wgcWindowSelection.CreateForWindow(window);
        if (selection is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.WgcUnavailable, windows, window, null);
        }

        var image = await _wgcCapture.CaptureOnceAsync(selection.Item, SingleCaptureTimeout);
        SelectedWindow = window;
        WgcSelection = selection;
        CapturedImage = image;
        return new CaptureOperationResult(CaptureOperationStatus.Success, windows, window, selection);
    }

    public async Task<CaptureOperationResult> CaptureManualAsync(Window owner)
    {
        var selection = await _wgcWindowSelection.PickWindowAsync(owner);
        if (selection is null)
        {
            return new CaptureOperationResult(CaptureOperationStatus.Canceled, [], null, null);
        }

        if (!selection.LooksLikeMabinogi)
        {
            return new CaptureOperationResult(CaptureOperationStatus.NotMabinogi, [], null, selection);
        }

        var windows = GetVisibleWindows();
        var window = MatchPickedWindow(windows, selection.DisplayName);
        var image = await _wgcCapture.CaptureOnceAsync(selection.Item, SingleCaptureTimeout);
        SelectedWindow = window;
        WgcSelection = selection;
        CapturedImage = image;
        return new CaptureOperationResult(CaptureOperationStatus.Success, windows, window, selection);
    }

    public BitmapSource Crop(BitmapSource source, Rect rect) => _windowCapture.Crop(source, rect);

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

    public void StopLiveWgcCapture() => _wgcCapture.StopLiveCapture();

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
        string displayName) =>
        windows.FirstOrDefault(item => item.IsPreferredMabinogiClient && MatchesDisplayName(item, displayName))
        ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi && MatchesDisplayName(item, displayName))
        ?? windows.FirstOrDefault(item => item.IsPreferredMabinogiClient)
        ?? windows.FirstOrDefault(item => item.LooksLikeMabinogi);

    private static bool MatchesDisplayName(GameWindowInfo window, string displayName) =>
        string.Equals(window.Title, displayName, StringComparison.OrdinalIgnoreCase)
        || displayName.Contains(window.Title, StringComparison.OrdinalIgnoreCase)
        || window.Title.Contains(displayName, StringComparison.OrdinalIgnoreCase);
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
