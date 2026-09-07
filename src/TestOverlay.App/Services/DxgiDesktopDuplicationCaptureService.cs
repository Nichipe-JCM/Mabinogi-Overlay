using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using TestOverlay.App.Models;
using TestOverlay.App.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace TestOverlay.App.Services;

public sealed class DxgiDesktopDuplicationCaptureService : IDisposable
{
    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
        D3DFeatureLevel.Level_10_1,
        D3DFeatureLevel.Level_10_0
    ];

    public DxgiVerificationResult Verify(GameWindowInfo window)
    {
        var geometry = ResolveGeometry(window);
        using var resources = CreateResources(geometry.MonitorHandle);
        return new DxgiVerificationResult(
            resources.OutputDescription.DeviceName,
            geometry.ClientWidth,
            geometry.ClientHeight,
            geometry.ClientScreenX,
            geometry.ClientScreenY,
            resources.OutputDescription.DesktopCoordinates.Left,
            resources.OutputDescription.DesktopCoordinates.Top,
            resources.OutputDescription.DesktopCoordinates.Right - resources.OutputDescription.DesktopCoordinates.Left,
            resources.OutputDescription.DesktopCoordinates.Bottom - resources.OutputDescription.DesktopCoordinates.Top);
    }

    private readonly object _sync = new();
    private DxgiResources? _resources;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private nint _monitor;
    private int _stagingWidth, _stagingHeight;
    private byte[] _pixels = [];
    private BitmapSource? _lastFrame;
    private CaptureGeometry? _lastGeometry;
    public int SessionCreationCount { get; private set; }

    public BitmapSource CaptureClientArea(GameWindowInfo window, int timeoutMilliseconds = 500)
    {
        lock (_sync)
        {
            var geometry = ResolveGeometry(window);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                EnsureSession(geometry.MonitorHandle);
                IDXGIResource? desktopResource = null;
                var acquired = false;
                var lost = false;
                try
                {
                    var result = _duplication!.AcquireNextFrame((uint)Math.Clamp(timeoutMilliseconds, 0, 500), out _, out desktopResource);
                    if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        if (_lastGeometry == geometry && _lastFrame is not null) return _lastFrame;
                        continue;
                    }
                    if (result.Code == Vortice.DXGI.ResultCode.AccessLost)
                    {
                        lost = true;
                        continue;
                    }
                    result.CheckError();
                    acquired = true;
                    using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    _lastFrame = CopyClientAreaToBitmap(texture, geometry);
                    _lastGeometry = geometry;
                    return _lastFrame;
                }
                catch
                {
                    lost = true;
                    throw;
                }
                finally
                {
                    desktopResource?.Dispose();
                    try { if (acquired) _duplication!.ReleaseFrame(); }
                    finally { if (lost) ResetCore(); }
                }
            }
            throw new TimeoutException("DXGI desktop duplication did not produce a frame before the timeout.");
        }
    }

    private void EnsureSession(nint monitor)
    {
        if (_resources is not null && _monitor == monitor) return;
        ResetCore();
        try
        {
            _resources = CreateResources(monitor);
            _duplication = _resources.Output.DuplicateOutput(_resources.Device);
            _monitor = monitor;
            SessionCreationCount++;
        }
        catch { ResetCore(); throw; }
    }

    public void Reset() { lock (_sync) ResetCore(); }
    public void Dispose() => Reset();

    private void ResetCore()
    {
        _staging?.Dispose(); _staging = null;
        _duplication?.Dispose(); _duplication = null;
        _resources?.Dispose(); _resources = null;
        _monitor = 0;
        _stagingWidth = _stagingHeight = 0;
        _lastFrame = null;
        _lastGeometry = null;
        _pixels = [];
    }

    private BitmapSource CopyClientAreaToBitmap(ID3D11Texture2D texture, CaptureGeometry geometry)
    {
        var resources = _resources!;
        var output = resources.OutputDescription.DesktopCoordinates;
        var sourceLeft = Math.Clamp(geometry.ClientScreenX - output.Left, 0, Math.Max(0, output.Right - output.Left - 1));
        var sourceTop = Math.Clamp(geometry.ClientScreenY - output.Top, 0, Math.Max(0, output.Bottom - output.Top - 1));
        var width = Math.Clamp(geometry.ClientWidth, 1, output.Right - output.Left - sourceLeft);
        var height = Math.Clamp(geometry.ClientHeight, 1, output.Bottom - output.Top - sourceTop);
        if (_staging is null || width != _stagingWidth || height != _stagingHeight)
        {
            _staging?.Dispose();
            _staging = null;
            _staging = resources.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
            });
            _stagingWidth = width; _stagingHeight = height;
            _pixels = new byte[checked(width * height * 4)];
        }
        var context = resources.Context;
        context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0,
            new Box(sourceLeft, sourceTop, 0, sourceLeft + width, sourceTop + height, 1));
        context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(nint.Add(mapped.DataPointer, y * (int)mapped.RowPitch), _pixels, y * stride, stride);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, _pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally { context.Unmap(_staging, 0); }
    }

    private static DxgiResources CreateResources(nint monitorHandle)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        if (description.Monitor != monitorHandle)
                        {
                            continue;
                        }

                        ID3D11Device device;
                        ID3D11DeviceContext context;
                        D3D11.D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            FeatureLevels,
                            out device,
                            out context).CheckError();
                        var output1 = output.QueryInterface<IDXGIOutput1>();
                        return new DxgiResources(device, context, output1, description);
                    }
                }
            }
        }

        throw new InvalidOperationException("DXGI output for the selected window monitor could not be found.");
    }

    private static CaptureGeometry ResolveGeometry(GameWindowInfo window)
    {
        if (!Win32Methods.GetClientRect(window.Handle, out var clientRect))
        {
            throw new InvalidOperationException("Selected window client rectangle could not be read.");
        }

        var origin = new Win32Methods.PointNative { X = 0, Y = 0 };
        if (!Win32Methods.ClientToScreen(window.Handle, ref origin))
        {
            throw new InvalidOperationException("Selected window client position could not be resolved.");
        }

        var monitor = Win32Methods.MonitorFromWindow(window.Handle, Win32Methods.MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            throw new InvalidOperationException("Selected window monitor could not be resolved.");
        }

        return new CaptureGeometry(monitor, origin.X, origin.Y, clientRect.Width, clientRect.Height);
    }

    private sealed record CaptureGeometry(nint MonitorHandle, int ClientScreenX, int ClientScreenY, int ClientWidth, int ClientHeight);

    private sealed record DxgiResources(
        ID3D11Device Device,
        ID3D11DeviceContext Context,
        IDXGIOutput1 Output,
        OutputDescription OutputDescription) : IDisposable
    {
        public void Dispose()
        {
            Output.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }
}

public sealed record DxgiVerificationResult(
    string OutputName,
    int ClientWidth,
    int ClientHeight,
    int ClientScreenX,
    int ClientScreenY,
    int OutputLeft,
    int OutputTop,
    int OutputWidth,
    int OutputHeight);
