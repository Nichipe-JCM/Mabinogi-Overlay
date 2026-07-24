using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TestOverlay.App.Services;

internal static partial class WindowCornerService
{
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    public static void ApplyStandardCorners(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            window.AllowsTransparency ||
            window.WindowStyle == WindowStyle.None)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowAttributeCornerPreference,
            ref preference,
            sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
