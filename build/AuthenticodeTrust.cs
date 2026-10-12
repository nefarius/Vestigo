using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Vestigo.Build;

static class AuthenticodeTrust
{
    const uint UiNone = 2;
    const uint RevokeNone = 0;
    const uint ChoiceFile = 1;
    const uint StateVerify = 1;
    const uint StateClose = 2;
    const uint RevocationCheckNone = 0x10;
    const uint CacheOnlyUrlRetrieval = 0x1000;

    public static void RequireTrustedPublisher(string path, string expectedSubject)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Signed file not found.", path);
        }

        int result = VerifyFile(path);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Authenticode verification failed for '{path}' (WinVerifyTrust 0x{result:X8}).");
        }

#pragma warning disable SYSLIB0057
        using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        string subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!string.Equals(subject, expectedSubject, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected publisher '{expectedSubject}' but '{path}' is signed by '{subject}'.");
        }
    }

    static int VerifyFile(string path)
    {
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        IntPtr pathPointer = Marshal.StringToCoTaskMemUni(path);
        IntPtr filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            WintrustFileInfo fileInfo = new()
            {
                StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                FilePath = pathPointer
            };
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            WintrustData data = new()
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfo = filePointer,
                StateAction = StateVerify,
                ProviderFlags = RevocationCheckNone | CacheOnlyUrlRetrieval
            };
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = StateClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
            Marshal.FreeCoTaskMem(filePointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
