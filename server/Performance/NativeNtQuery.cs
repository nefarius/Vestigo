using System.Runtime.InteropServices;

namespace Vestigo.Performance;

/// <summary>
///     ntdll fallbacks. NtQueryTimerResolution and NtQuerySystemInformation are not in the pinned
///     CsWin32 Win32 metadata (see docs/architecture.md Diagnostics notes for the same gap).
/// </summary>
internal static partial class NativeNtQuery
{
    public const int SystemProcessInformation = 5;
    public const int SystemPoolTagInformation = 22;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusSuccess = 0;

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int NtQueryTimerResolution(out uint maximum, out uint minimum, out uint current);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int NtQuerySystemInformation(int classId, nint buffer, int length, out int returned);
}
