using System.Runtime.InteropServices;

namespace TestOverlay.App.Native;

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMemoryBufferByteAccess
{
    void GetBuffer(out nint buffer, out uint capacity);
}
