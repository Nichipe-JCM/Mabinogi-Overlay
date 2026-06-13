using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using TestOverlay.App.Models;
using Windows.Graphics.Capture;
using WinRT;
using WinRT.Interop;

namespace TestOverlay.App.Services;

public sealed class WgcWindowSelectionService
{
    public async Task<WgcSelectionResult?> PickWindowAsync(Window owner)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, new WindowInteropHelper(owner).Handle);
        var item = await picker.PickSingleItemAsync();
        return item is null
            ? null
            : new WgcSelectionResult(item.DisplayName, item.Size.Width, item.Size.Height, item);
    }

    public WgcSelectionResult? CreateForWindow(GameWindowInfo window)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        var item = CreateItemForWindow(window.Handle);
        return new WgcSelectionResult(
            window.Title,
            item.Size.Width,
            item.Size.Height,
            item,
            window.LooksLikeMabinogi);
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint windowHandle)
    {
        var className = nint.Zero;
        var factoryPointer = nint.Zero;
        var itemPointer = nint.Zero;
        try
        {
            var hresult = WindowsCreateString(
                GraphicsCaptureItemRuntimeClassName,
                GraphicsCaptureItemRuntimeClassName.Length,
                out className);
            ThrowIfFailed(hresult);

            var factoryGuid = GraphicsCaptureItemInteropGuid;
            hresult = RoGetActivationFactory(className, ref factoryGuid, out factoryPointer);
            ThrowIfFailed(hresult);

            var factory = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                factoryPointer,
                typeof(IGraphicsCaptureItemInterop));
            var itemGuid = GraphicsCaptureItemGuid;
            hresult = factory.CreateForWindow(windowHandle, ref itemGuid, out itemPointer);
            ThrowIfFailed(hresult);

            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != nint.Zero)
            {
                Marshal.Release(itemPointer);
            }

            if (factoryPointer != nint.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            if (className != nint.Zero)
            {
                _ = WindowsDeleteString(className);
            }
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    private const string GraphicsCaptureItemRuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, ref Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);
}
