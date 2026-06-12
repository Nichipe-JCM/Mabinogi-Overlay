using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace TestOverlay.App.Native;

internal static partial class Direct3D11Interop
{
    private static readonly Guid IdxgiDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    public static IDirect3DDevice CreateDevice()
    {
        var result = D3D11CreateDevice(
            0,
            D3DDriverType.Hardware,
            0,
            D3D11CreateDeviceFlags.BgraSupport,
            0,
            0,
            7,
            out var d3dDevice,
            out _,
            out var context);

        ThrowIfFailed(result, "D3D11CreateDevice failed.");

        try
        {
            var dxgiDeviceGuid = IdxgiDeviceGuid;
            result = Marshal.QueryInterface(d3dDevice, ref dxgiDeviceGuid, out var dxgiDevice);
            ThrowIfFailed(result, "IDXGIDevice query failed.");

            try
            {
                result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
                ThrowIfFailed(result, "CreateDirect3D11DeviceFromDXGIDevice failed.");

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != 0)
            {
                Marshal.Release(context);
            }

            if (d3dDevice != 0)
            {
                Marshal.Release(d3dDevice);
            }
        }
    }

    public static IDirect3DDevice CreateDeviceFromDxgiDevice(nint dxgiDevice)
    {
        var result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        ThrowIfFailed(result, "CreateDirect3D11DeviceFromDXGIDevice failed.");

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        nint pAdapter,
        D3DDriverType driverType,
        nint software,
        D3D11CreateDeviceFlags flags,
        nint pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out int pFeatureLevel,
        out nint ppImmediateContext);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private static void ThrowIfFailed(int hresult, string message)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
            throw new InvalidOperationException(message);
        }
    }

    private enum D3DDriverType : uint
    {
        Hardware = 1
    }

    [Flags]
    private enum D3D11CreateDeviceFlags : uint
    {
        BgraSupport = 0x20
    }
}
