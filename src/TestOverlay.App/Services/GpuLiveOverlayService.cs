using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using TestOverlay.App.Models;
using TestOverlay.App.Native;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace TestOverlay.App.Services;

public sealed class GpuLiveOverlayService : IDisposable
{
    private readonly object _renderLock = new();
    private readonly IReadOnlyList<OverlaySlot> _slots;
    private readonly int _surfaceWidth;
    private readonly int _surfaceHeight;
    private readonly long _minFrameTicks;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGIDevice? _dxgiDevice;
    private IDXGIFactory2? _dxgiFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _targetBitmap;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private IDirect3DDevice? _winRtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private long _lastPresentedTicks;
    private int _isRendering;
    private bool _isDisposed;

    public GpuLiveOverlayService(
        nint overlayHandle,
        int surfaceWidth,
        int surfaceHeight,
        GraphicsCaptureItem captureItem,
        IReadOnlyList<OverlaySlot> slots,
        int maxFps)
    {
        if (overlayHandle == nint.Zero)
        {
            throw new ArgumentException("Overlay handle is not initialized.", nameof(overlayHandle));
        }

        _surfaceWidth = Math.Max(1, surfaceWidth);
        _surfaceHeight = Math.Max(1, surfaceHeight);
        _slots = slots;
        _minFrameTicks = Stopwatch.Frequency / Math.Clamp(maxFps, 1, 240);

        InitializeDevices(overlayHandle);
        InitializeCapture(captureItem, maxFps);
    }

    public Exception? LastException { get; private set; }

    public bool IsRunning => _session is not null && !_isDisposed;

    public void Start()
    {
        ThrowIfDisposed();
        _session?.StartCapture();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= FramePool_FrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _winRtDevice?.Dispose();
        _targetBitmap?.Dispose();
        _compositionVisual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _swapChain?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _dxgiFactory?.Dispose();
        _dxgiDevice?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();

        _session = null;
        _framePool = null;
        _winRtDevice = null;
        _targetBitmap = null;
        _compositionVisual = null;
        _compositionTarget = null;
        _compositionDevice = null;
        _swapChain = null;
        _d2dContext = null;
        _d2dDevice = null;
        _dxgiFactory = null;
        _dxgiDevice = null;
        _d3dContext = null;
        _d3dDevice = null;
    }

    private void InitializeDevices(nint overlayHandle)
    {
        _d3dDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            D3DFeatureLevel.Level_11_1,
            D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1,
            D3DFeatureLevel.Level_10_0);
        _d3dContext = _d3dDevice.ImmediateContext;

        using (var multithread = _d3dDevice.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _d2dDevice = D2D1.D2D1CreateDevice(_dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        var swapChainDescription = new SwapChainDescription1(
            (uint)_surfaceWidth,
            (uint)_surfaceHeight,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            Vortice.DXGI.AlphaMode.Premultiplied,
            SwapChainFlags.None);

        _swapChain = _dxgiFactory.CreateSwapChainForComposition(_d3dDevice, swapChainDescription, null);

        using (var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0))
        {
            var targetProperties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96,
                96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);
            _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, targetProperties);
        }

        _d2dContext.Target = _targetBitmap;
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(overlayHandle, true, out _compositionTarget).CheckError();
        _compositionDevice.CreateVisual(out _compositionVisual).CheckError();
        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _compositionDevice.Commit().CheckError();

        _winRtDevice = Direct3D11Interop.CreateDeviceFromDxgiDevice(_dxgiDevice.NativePointer);
    }

    private void InitializeCapture(GraphicsCaptureItem captureItem, int maxFps)
    {
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            captureItem.Size);
        _session = _framePool.CreateCaptureSession(captureItem);
        _session.IsCursorCaptureEnabled = false;
        TrySetSessionProperty(_session, "IsBorderRequired", false);
        TrySetSessionProperty(_session, "MinUpdateInterval", TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(maxFps, 1, 240)));
        _framePool.FrameArrived += FramePool_FrameArrived;
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_isDisposed)
        {
            using var _ = sender.TryGetNextFrame();
            return;
        }

        var now = _frameClock.ElapsedTicks;
        if (now - Interlocked.Read(ref _lastPresentedTicks) < _minFrameTicks)
        {
            using var droppedFrame = sender.TryGetNextFrame();
            return;
        }

        if (Interlocked.Exchange(ref _isRendering, 1) == 1)
        {
            using var droppedFrame = sender.TryGetNextFrame();
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            RenderFrame(frame);
            Interlocked.Exchange(ref _lastPresentedTicks, _frameClock.ElapsedTicks);
            LastException = null;
        }
        catch (ObjectDisposedException)
        {
            // The overlay was stopped while WGC was unwinding a frame callback.
        }
        catch (Exception ex)
        {
            LastException = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _isRendering, 0);
        }
    }

    private void RenderFrame(Direct3D11CaptureFrame frame)
    {
        if (_d2dContext is null || _swapChain is null)
        {
            return;
        }

        lock (_renderLock)
        {
            var texturePointer = Direct3DSurfaceInterop.GetD3D11Texture2DPointer(frame.Surface);
            using var texture = new ID3D11Texture2D(texturePointer);
            using var frameSurface = texture.QueryInterface<IDXGISurface>();
            var sourceProperties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                96,
                96,
                BitmapOptions.None);
            using var frameBitmap = _d2dContext.CreateBitmapFromDxgiSurface(frameSurface, sourceProperties);

            _d2dContext.BeginDraw();
            _d2dContext.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));

            foreach (var slot in _slots)
            {
                DrawSlot(frameBitmap, slot);
            }

            _d2dContext.EndDraw();
            _swapChain.Present(0, PresentFlags.None);
        }
    }

    private void DrawSlot(ID2D1Bitmap1 frameBitmap, OverlaySlot slot)
    {
        if (_d2dContext is null)
        {
            return;
        }

        var source = slot.Source.SourceRect;
        var destination = slot.OverlayRect;
        if (source.Width <= 0 ||
            source.Height <= 0 ||
            destination.Width <= 0 ||
            destination.Height <= 0)
        {
            return;
        }

        var sourceRect = ToRawRect(source);
        var destinationRect = ToRawRect(destination);
        var opacity = (float)Math.Clamp(slot.Opacity, 0.05, 1);
        _d2dContext.DrawBitmap(
            frameBitmap,
            destinationRect,
            opacity,
            Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
            sourceRect,
            null);
    }

    private static RawRectF ToRawRect(Rect rect) =>
        new(
            (float)rect.X,
            (float)rect.Y,
            (float)(rect.X + rect.Width),
            (float)(rect.Y + rect.Height));

    private static void TrySetSessionProperty(GraphicsCaptureSession session, string propertyName, object value)
    {
        try
        {
            var property = typeof(GraphicsCaptureSession).GetProperty(propertyName);
            property?.SetValue(session, value);
        }
        catch
        {
            // Windows build or user consent can make some WGC properties unavailable.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuLiveOverlayService));
        }
    }
}
