using Windows.Graphics.Capture;

namespace TestOverlay.App.Services;

public sealed class WgcSupportService
{
    public bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }
}
