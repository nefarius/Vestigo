using Windows.Win32;
using Windows.Win32.Devices.Display;

namespace Vestigo.Desktop;

/// <summary>Reads refresh, active mode, rotation, scaling, adapter LUID, and advanced color for one CCD path.</summary>
internal static class DisplaySignalReader
{
    internal static int? Count(uint value) => value is 0 or > int.MaxValue ? null : (int)value;

    internal static double? RefreshHz(uint numerator, uint denominator) =>
        denominator == 0 || numerator == 0 ? null : numerator / (double)denominator;

    internal static string? Luid(int high, uint low) => high == 0 && low == 0 ? null : $"{high:X8}:{low:X8}";

    internal static string? TrimPrefix(string? name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    public static unsafe DisplaySignal Read(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        int? width = null;
        int? height = null;
        uint targetIndex = path.targetInfo.modeInfoIdx;
        if (targetIndex < (uint)modes.Length &&
            modes[targetIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            DISPLAYCONFIG_2DREGION active = modes[targetIndex].targetMode.targetVideoSignalInfo.activeSize;
            width = Count(active.cx);
            height = Count(active.cy);
        }

        if (width is null || height is null)
        {
            uint sourceIndex = path.sourceInfo.modeInfoIdx;
            if (sourceIndex < (uint)modes.Length &&
                modes[sourceIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                DISPLAYCONFIG_SOURCE_MODE source = modes[sourceIndex].sourceMode;
                width ??= Count(source.width);
                height ??= Count(source.height);
            }
        }

        bool? supported = null;
        bool? enabled = null;
        int? bits = null;
        try
        {
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color = default;
            color.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            color.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
            color.header.adapterId = path.targetInfo.adapterId;
            color.header.id = path.targetInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(ref UnsafeHeader(ref color)) == 0)
            {
                supported = color.advancedColorSupported;
                enabled = color.advancedColorEnabled;
                bits = Count(color.bitsPerColorChannel);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Advanced color is absent on this build of Windows.
        }

        return new DisplaySignal(
            Luid(path.sourceInfo.adapterId.HighPart, path.sourceInfo.adapterId.LowPart),
            RefreshHz(path.targetInfo.refreshRate.Numerator, path.targetInfo.refreshRate.Denominator),
            width,
            height,
            TrimPrefix(path.targetInfo.rotation.ToString(), "DISPLAYCONFIG_ROTATION_"),
            TrimPrefix(path.targetInfo.scaling.ToString(), "DISPLAYCONFIG_SCALING_"),
            supported,
            enabled,
            bits);
    }

    private static ref DISPLAYCONFIG_DEVICE_INFO_HEADER UnsafeHeader(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color) =>
        ref System.Runtime.CompilerServices.Unsafe.As<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref color);
}
