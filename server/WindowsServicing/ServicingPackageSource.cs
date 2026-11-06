using System.Runtime.InteropServices;

using Windows.Win32.Foundation;

namespace Vestigo.WindowsServicing;

public interface IServicingPackageSource
{
    BoundedList<ServicingPackage> List(int limit);
}

/// <summary>Documented DISM package API; no localized output parsing and no repair entry points.</summary>
public sealed class ServicingPackageSource : IServicingPackageSource
{
    public BoundedList<ServicingPackage> List(int limit)
    {
        DismInitialize(0, null, null).ThrowOnFailure();
        uint session = 0;
        bool opened = false;
        nint buffer = 0;
        try
        {
            DismOpenSession("DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}", null, null, out session).ThrowOnFailure();
            opened = true;
            DismGetPackages(session, out buffer, out uint count).ThrowOnFailure();
            List<ServicingPackage> packages = [];
            int size = Marshal.SizeOf<Package>();
            for (int i = 0; i < Math.Min(count, (uint)limit); i++)
            {
                Package package = Marshal.PtrToStructure<Package>(buffer + i * size);
                packages.Add(new(package.Name.ToString() ?? "", StateName(package.State), package.State, package.ReleaseType));
            }
            return new(packages, checked((int)count), count > packages.Count);
        }
        finally
        {
            if (buffer != 0) DismDelete(buffer);
            if (opened) DismCloseSession(session);
            DismShutdown();
        }
    }

    internal static string StateName(int state) => state switch
    {
        0 => "notPresent", 1 => "uninstallPending", 2 => "staged", 3 => "removed",
        4 => "installed", 5 => "installPending", 6 => "superseded",
        7 => "partiallyInstalled", _ => "unknown"
    };

    // CsWin32 0.3.367 with this project's pinned metadata reports PInvoke001 for
    // DismInitialize, DismOpenSession, DismGetPackages, DismDelete, DismCloseSession,
    // DismShutdown, DismPackage, DismPackageFeatureState, DismReleaseType, DismLogLevel
    // and DISM_ONLINE_IMAGE. Retain only these DISM-specific ABI declarations here.
    // DismSession is UINT; DismPackage contains a borrowed PCWSTR, two 32-bit enums and
    // SYSTEMTIME. DismDelete releases the returned array and its package names;
    // close the image session before DismShutdown. Shared Windows types are generated.
    // Metadata coverage: https://github.com/microsoft/win32metadata/issues/2265
    [StructLayout(LayoutKind.Sequential)]
    private struct Package
    {
        public PCWSTR Name;
        public int State;
        public int ReleaseType;
        public SYSTEMTIME InstallTime;
    }

    [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismInitialize(int level, string? logPath, string? scratchDirectory);
    [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismGetPackages(uint session, out nint packages, out uint count);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismDelete(nint value);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismCloseSession(uint session);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DismShutdown();
}
