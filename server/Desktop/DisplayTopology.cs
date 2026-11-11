using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Vestigo.Desktop;

/// <summary>
///     GDI monitors plus the physical targets <c>QueryDisplayConfig</c> reports for each source. Falls back to
///     <c>EnumDisplayDevices</c> when the CCD API is unavailable (session 0, some RDP sessions).
/// </summary>
public static class DisplayTopology
{
    public static DisplayListResult List(bool includeModes = false) =>
        new(Enumerate(includeModes), Process.GetCurrentProcess().SessionId);

    public static IReadOnlyList<DisplayInfo> Enumerate(bool includeModes = false)
    {
        nint previousDpi = WindowEnumerator.SetDpiAware();
        try
        {
            List<GdiMonitor> monitors = EnumerateGdiMonitors();
            IReadOnlyDictionary<string, DisplayExtras> extras = TryQueryDisplayConfig(includeModes)
                ?? EnumerateDisplayDevices();

            return monitors
                .Select(monitor =>
                {
                    extras.TryGetValue(monitor.DeviceName, out DisplayExtras? extra);
                    return new DisplayInfo(
                        monitor.DeviceName,
                        monitor.Primary,
                        monitor.Bounds,
                        monitor.WorkArea,
                        monitor.Dpi,
                        extra?.AdapterName,
                        extra?.Monitors ?? []);
                })
                .ToList();
        }
        finally
        {
            WindowEnumerator.RestoreDpi(previousDpi);
        }
    }

    private sealed record GdiMonitor(string DeviceName, bool Primary, DesktopRect Bounds, DesktopRect WorkArea, int Dpi);

    private sealed record DisplayExtras(string? AdapterName, IReadOnlyList<PhysicalMonitor> Monitors);

    private static List<GdiMonitor> EnumerateGdiMonitors()
    {
        List<GdiMonitor> monitors = [];
        GCHandle handle = GCHandle.Alloc(monitors);
        try
        {
            unsafe
            {
                PInvoke.EnumDisplayMonitors(default, (RECT*)null, &MonitorCallback, (LPARAM)GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe BOOL MonitorCallback(HMONITOR monitor, HDC hdc, RECT* clip, LPARAM parameter)
    {
        try
        {
            List<GdiMonitor> monitors = (List<GdiMonitor>)GCHandle.FromIntPtr((nint)parameter.Value).Target!;
            if (!TryGetMonitorInfo(monitor, out MONITORINFOEXW info))
            {
                return true;
            }

            string device = info.szDevice.AsReadOnlySpan().ToString().TrimEnd('\0');
            if (string.IsNullOrEmpty(device))
            {
                return true;
            }

            RECT bounds = info.monitorInfo.rcMonitor;
            RECT work = info.monitorInfo.rcWork;
            monitors.Add(new GdiMonitor(
                device,
                (info.monitorInfo.dwFlags & 1u) != 0,
                new DesktopRect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top),
                new DesktopRect(work.left, work.top, work.right - work.left, work.bottom - work.top),
                MonitorDpi(monitor)));
        }
        catch (Exception)
        {
            // Never let an exception cross the native boundary.
        }

        return true;
    }

    internal static bool TryGetMonitorInfo(HMONITOR monitor, out MONITORINFOEXW info)
    {
        info = default;
        info.monitorInfo.cbSize = (uint)Unsafe.SizeOf<MONITORINFOEXW>();
        return PInvoke.GetMonitorInfo(monitor, ref Unsafe.As<MONITORINFOEXW, MONITORINFO>(ref info));
    }

    private static int MonitorDpi(HMONITOR monitor)
    {
        if (!HasMonitorDpiApi())
        {
            return 96;
        }

        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0)
        {
            return (int)dpiX;
        }

        return 96;
    }

    [SupportedOSPlatformGuard("windows8.1")]
    private static bool HasMonitorDpiApi() => OperatingSystem.IsWindowsVersionAtLeast(6, 3);

    private static unsafe IReadOnlyDictionary<string, DisplayExtras>? TryQueryDisplayConfig(bool includeModes)
    {
        try
        {
            uint pathCount;
            uint modeCount;
            WIN32_ERROR size = PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
            if (size != WIN32_ERROR.NO_ERROR || pathCount == 0)
            {
                return null;
            }

            DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR query;
            fixed (DISPLAYCONFIG_PATH_INFO* pathPointer = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modePointer = modes)
            {
                query = PInvoke.QueryDisplayConfig(
                    QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    &pathCount,
                    pathPointer,
                    &modeCount,
                    modePointer,
                    null);
            }

            if (query != WIN32_ERROR.NO_ERROR)
            {
                return null;
            }

            Dictionary<string, List<PhysicalMonitor>> monitors = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string?> adapters = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < pathCount; index++)
            {
                DISPLAYCONFIG_PATH_INFO path = paths[index];
                DISPLAYCONFIG_SOURCE_DEVICE_NAME source = default;
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (GetDeviceInfo(ref source) != 0)
                {
                    continue;
                }

                string gdi = source.viewGdiDeviceName.AsReadOnlySpan().ToString().TrimEnd('\0');
                if (string.IsNullOrEmpty(gdi))
                {
                    continue;
                }

                DISPLAYCONFIG_ADAPTER_NAME adapter = default;
                adapter.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
                adapter.header.size = (uint)sizeof(DISPLAYCONFIG_ADAPTER_NAME);
                adapter.header.adapterId = path.sourceInfo.adapterId;
                string? adapterName = GetDeviceInfo(ref adapter) == 0
                    ? NullIfEmpty(adapter.adapterDevicePath.AsReadOnlySpan().ToString().TrimEnd('\0'))
                    : null;
                adapters[gdi] = adapterName;

                DISPLAYCONFIG_TARGET_DEVICE_NAME target = default;
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                if (GetDeviceInfo(ref target) != 0)
                {
                    continue;
                }

                if (!monitors.TryGetValue(gdi, out List<PhysicalMonitor>? list))
                {
                    list = [];
                    monitors[gdi] = list;
                }

                list.Add(new PhysicalMonitor(
                    NullIfEmpty(target.monitorFriendlyDeviceName.AsReadOnlySpan().ToString().TrimEnd('\0')),
                    NullIfEmpty(target.monitorDevicePath.AsReadOnlySpan().ToString().TrimEnd('\0')),
                    DecodeManufacturer(target.edidManufactureId),
                    target.edidProductCodeId == 0 ? null : "0x" + target.edidProductCodeId.ToString("X4"),
                    FormatOutputTechnology(target.outputTechnology.ToString()),
                    target.connectorInstance,
                    includeModes ? DisplaySignalReader.Read(path, modes) : null));
            }

            return monitors.Keys
                .Concat(adapters.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    name => name,
                    name => new DisplayExtras(
                        adapters.GetValueOrDefault(name),
                        monitors.GetValueOrDefault(name) ?? []),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static unsafe IReadOnlyDictionary<string, DisplayExtras> EnumerateDisplayDevices()
    {
        Dictionary<string, DisplayExtras> extras = new(StringComparer.OrdinalIgnoreCase);
        DISPLAY_DEVICEW adapter = default;
        adapter.cb = (uint)sizeof(DISPLAY_DEVICEW);
        for (uint index = 0; PInvoke.EnumDisplayDevices((string?)null, index, ref adapter, 0); index++)
        {
            string gdi = adapter.DeviceName.AsReadOnlySpan().ToString().TrimEnd('\0');
            if (string.IsNullOrEmpty(gdi))
            {
                continue;
            }

            string? adapterName = NullIfEmpty(adapter.DeviceString.AsReadOnlySpan().ToString().TrimEnd('\0'));
            List<PhysicalMonitor> monitors = [];
            DISPLAY_DEVICEW monitor = default;
            monitor.cb = (uint)sizeof(DISPLAY_DEVICEW);
            for (uint child = 0; PInvoke.EnumDisplayDevices(gdi, child, ref monitor, PInvoke.EDD_GET_DEVICE_INTERFACE_NAME); child++)
            {
                monitors.Add(new PhysicalMonitor(
                    NullIfEmpty(monitor.DeviceString.AsReadOnlySpan().ToString().TrimEnd('\0')),
                    NullIfEmpty(monitor.DeviceID.AsReadOnlySpan().ToString().TrimEnd('\0')),
                    null,
                    null,
                    null,
                    child));
                monitor.cb = (uint)sizeof(DISPLAY_DEVICEW);
            }

            extras[gdi] = new DisplayExtras(adapterName, monitors);
            adapter.cb = (uint)sizeof(DISPLAY_DEVICEW);
        }

        return extras;
    }

    private static int GetDeviceInfo<T>(ref T packet) where T : unmanaged =>
        PInvoke.DisplayConfigGetDeviceInfo(ref Unsafe.As<T, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref packet));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static string? DecodeManufacturer(ushort id)
    {
        if (id == 0)
        {
            return null;
        }

        int first = ((id >> 10) & 0x1F) + 64;
        int second = ((id >> 5) & 0x1F) + 64;
        int third = (id & 0x1F) + 64;
        if (first is < 'A' or > 'Z' || second is < 'A' or > 'Z' || third is < 'A' or > 'Z')
        {
            return "0x" + id.ToString("X4");
        }

        return string.Create(3, (first, second, third), static (span, letters) =>
        {
            span[0] = (char)letters.first;
            span[1] = (char)letters.second;
            span[2] = (char)letters.third;
        });
    }

    private static string? FormatOutputTechnology(string name)
    {
        const string prefix = "DISPLAYCONFIG_OUTPUT_TECHNOLOGY_";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }
}
