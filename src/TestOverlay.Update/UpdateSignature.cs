using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace TestOverlay.Update;

public static class UpdateSignature
{
    // Existing project signing identity. Rotation requires a reviewed updater release.
    public const string SignerThumbprint = "5E742A65875200203E2C7279B1DDE1331B5B617B";
    public static void Verify(string path)
    {
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (!certificate.Thumbprint.Equals(SignerThumbprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected executable signer.");
        var info = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = Path.GetFullPath(path) };
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
        Marshal.StructureToPtr(info, infoPointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = infoPointer, StateAction = 1, ProviderFlags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(new nint(-1), ref action, ref data);
            // Only the known self-signed root exception is accepted; hash, signature,
            // expiry, revocation and all other failures remain fatal.
            if (status != 0 && status != unchecked((int)0x800B0109)) throw new InvalidDataException($"Executable signature verification failed: 0x{status:X8}.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfoNative>(infoPointer);
            Marshal.FreeHGlobal(infoPointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoNative { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint FileHandle; public nint KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public nint Policy; public nint Sip; public uint UiChoice; public uint Revocation;
        public uint UnionChoice; public nint FileInfo; public uint StateAction; public nint StateData;
        public nint Url; public uint ProviderFlags; public uint UiContext; public nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
