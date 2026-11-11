using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Dism;

namespace Vestigo.WindowsServicing;

/// <summary>Restricts the managed wrapper's unrestricted DISM imports to the Windows system directory.</summary>
internal static class SystemDismLibrary
{
    static SystemDismLibrary() => NativeLibrary.SetDllImportResolver(typeof(DismApi).Assembly, Resolve);

    // Trigger resolver registration before any Microsoft.Dism native calls. Never use
    // InitializeEx/ADK generation selection, which has its own native loading behavior.
    internal static void Configure() { }

    internal static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals("DismApi", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("DismApi.dll", StringComparison.OrdinalIgnoreCase))
            throw new DllNotFoundException("Unexpected Microsoft.Dism native dependency.");

        // Do not return zero: that would enable the runtime's default search path.
        // The runtime caches P/Invoke bindings; keep the module loaded for process lifetime.
        return Library.Handle;
    }

    private static class Library
    {
        // Use the full path as well as search flags so an already loaded module with
        // the same basename cannot satisfy this request from another directory.
        internal static readonly nint Handle = NativeLibrary.Load(
            Path.Combine(Environment.SystemDirectory, "DismApi.dll"),
            typeof(DismApi).Assembly, DllImportSearchPath.System32);
    }
}
