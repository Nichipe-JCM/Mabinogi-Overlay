using System.Windows;
using System.Windows.Interop;
using TestOverlay.App.Models;
using Windows.Graphics.Capture;
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
}
