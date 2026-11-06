using System.Runtime.InteropServices;

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
        Marshal.ThrowExceptionForHR(DismInitialize(0, null, null));
        uint session = 0;
        bool opened = false;
        nint buffer = 0;
        try
        {
            Marshal.ThrowExceptionForHR(DismOpenSession("DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}", null, null, out session));
            opened = true;
            Marshal.ThrowExceptionForHR(DismGetPackages(session, out buffer, out uint count));
            List<ServicingPackage> packages = [];
            int size = Marshal.SizeOf<Package>();
            for (int i = 0; i < Math.Min(count, (uint)limit); i++)
            {
                Package package = Marshal.PtrToStructure<Package>(buffer + i * size);
                packages.Add(new(Marshal.PtrToStringUni(package.Name) ?? "", StateName(package.State), package.State, package.ReleaseType));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Package
    {
        public nint Name;
        public int State;
        public int ReleaseType;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismInitialize(int level, string? logPath, string? scratchDirectory);
    [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismGetPackages(uint session, out nint packages, out uint count);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismDelete(nint value);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismCloseSession(uint session);
    [DllImport("DismApi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismShutdown();
}
