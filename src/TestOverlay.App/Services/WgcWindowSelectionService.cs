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
#pragma warning disable CS0618
        using var factoryReference = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")
            .As<IGraphicsCaptureItemInterop>();
#pragma warning restore CS0618
        var factory = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
            factoryReference.ThisPtr,
            typeof(IGraphicsCaptureItemInterop));
        var itemGuid = GraphicsCaptureItemGuid;
        var hresult = factory.CreateForWindow(windowHandle, ref itemGuid, out var itemPointer);
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != nint.Zero)
            {
                Marshal.Release(itemPointer);
            }
        }
    }

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

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
}
