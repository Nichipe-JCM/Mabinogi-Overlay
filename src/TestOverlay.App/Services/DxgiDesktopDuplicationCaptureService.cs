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

public sealed class DxgiDesktopDuplicationCaptureService
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

    public BitmapSource CaptureClientArea(GameWindowInfo window, int timeoutMilliseconds = 500)
    {
        var geometry = ResolveGeometry(window);
        using var resources = CreateResources(geometry.MonitorHandle);
        using var duplication = resources.Output.DuplicateOutput(resources.Device);

        IDXGIResource? desktopResource = null;
        var frameAcquired = false;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = duplication.AcquireNextFrame((uint)timeoutMilliseconds, out _, out desktopResource);
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    continue;
                }

                result.CheckError();
                frameAcquired = true;
                using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                return CopyClientAreaToBitmap(resources.Device, resources.Context, desktopTexture, resources.OutputDescription, geometry);
            }

            throw new TimeoutException("DXGI desktop duplication did not produce a frame before the timeout.");
        }
        finally
        {
            desktopResource?.Dispose();
            if (frameAcquired)
            {
                duplication.ReleaseFrame();
            }
        }
    }

    private static BitmapSource CopyClientAreaToBitmap(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11Texture2D desktopTexture,
        OutputDescription outputDescription,
        CaptureGeometry geometry)
    {
        var outputLeft = outputDescription.DesktopCoordinates.Left;
        var outputTop = outputDescription.DesktopCoordinates.Top;
        var sourceLeft = Math.Clamp(geometry.ClientScreenX - outputLeft, 0, Math.Max(0, outputDescription.DesktopCoordinates.Right - outputLeft - 1));
        var sourceTop = Math.Clamp(geometry.ClientScreenY - outputTop, 0, Math.Max(0, outputDescription.DesktopCoordinates.Bottom - outputTop - 1));
        var width = Math.Clamp(geometry.ClientWidth, 1, outputDescription.DesktopCoordinates.Right - outputLeft - sourceLeft);
        var height = Math.Clamp(geometry.ClientHeight, 1, outputDescription.DesktopCoordinates.Bottom - outputTop - sourceTop);

        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        var sourceBox = new Box(sourceLeft, sourceTop, 0, sourceLeft + width, sourceTop + height, 1);
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, desktopTexture, 0, sourceBox);
        context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(nint.Add(mapped.DataPointer, y * (int)mapped.RowPitch), pixels, y * stride, stride);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
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
