using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace TestOverlay.App.Native;

internal static class Direct3DSurfaceInterop
{
    private static readonly Guid Id3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static nint GetD3D11Texture2DPointer(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = Id3D11Texture2DGuid;
        var result = access.GetInterface(ref iid, out var nativePointer);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        return nativePointer;
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out nint p);
    }
}
