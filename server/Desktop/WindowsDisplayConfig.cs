using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Vestigo.Desktop;

/// <summary>Reads and applies the active CCD topology. It does not create virtual displays.</summary>
public static class WindowsDisplayConfig
{
    public static OperationResult<DisplayLayoutSnapshot> Capture()
    {
        using PhysicalDpiLease dpi = new WindowsWindowInspectionSource().EnterPhysicalCoordinates();
        if (!dpi.Established)
        {
            return OperationResult<DisplayLayoutSnapshot>.Fail(
                dpi.ErrorCode ?? "dpi_context_unavailable",
                dpi.Error ?? "The physical DPI context could not be established.");
        }

        if (!TryQuery(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes, out uint pathCount, out string? error))
        {
            return OperationResult<DisplayLayoutSnapshot>.Fail("display_query_failed", error!);
        }

        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        List<DisplayPathLayout> layouts = [];
        for (int index = 0; index < pathCount; index++)
        {
            if (!TryDescribe(paths[index], modes, displays, out DisplayPathLayout? layout))
            {
                continue;
            }

            layouts.Add(layout!);
        }

        return OperationResult<DisplayLayoutSnapshot>.Ok(DisplayLayoutPlanner.WithFingerprint(new DisplayLayoutSnapshot(
            DateTimeOffset.UtcNow,
            Process.GetCurrentProcess().SessionId,
            DesktopCoordinates.PhysicalScreenPixels,
            string.Empty,
            layouts)));
    }

    public static OperationResult<DisplayLayoutApplyResult> Apply(DisplayLayoutRequest request)
    {
        OperationResult<DisplayLayoutSnapshot> before = Capture();
        if (!before.Success)
        {
            return before.FailAs<DisplayLayoutApplyResult>();
        }

        DisplayLayoutValidation validation = DisplayLayoutPlanner.Validate(before.Value!, request.Targets);
        if (!validation.Valid)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, false, request.Persistent, null, "invalid_request", string.Join(' ', validation.Errors), before.Value, null));
        }

        if (!TryQuery(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes, out uint pathCount, out string? error))
        {
            return OperationResult<DisplayLayoutApplyResult>.Fail("display_query_failed", error!);
        }

        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        for (int index = 0; index < pathCount; index++)
        {
            if (!TryDescribe(paths[index], modes, displays, out DisplayPathLayout? layout))
            {
                continue;
            }

            DisplayTargetChange? change = request.Targets.FirstOrDefault(candidate => DisplayLayoutPlanner.Matches(layout!.Identity, candidate));
            if (change is null)
            {
                continue;
            }

            if (!TryPatch(ref paths[index], modes, change))
            {
                return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                    false, false, request.Persistent, null, "display_mode_unavailable",
                    "The active path has no source or target mode that can be updated.", before.Value, null));
            }
        }

        SET_DISPLAY_CONFIG_FLAGS validateFlags = SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE
            | SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES
            | SET_DISPLAY_CONFIG_FLAGS.SDC_VIRTUAL_MODE_AWARE;
        WIN32_ERROR validated = ApplyNative(pathCount, paths, (uint)modes.Length, modes, validateFlags);
        if (validated != WIN32_ERROR.NO_ERROR)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, false, request.Persistent, (int)validated, "display_config_rejected",
                "SetDisplayConfig validation failed.", before.Value, null));
        }

        if (request.ValidateOnly)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, true, request.Persistent, null, null, null, before.Value, before.Value));
        }

        SET_DISPLAY_CONFIG_FLAGS applyFlags = validateFlags | SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY;
        if (request.Persistent)
        {
            applyFlags |= SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE;
        }

        WIN32_ERROR applied = ApplyNative(pathCount, paths, (uint)modes.Length, modes, applyFlags);
        OperationResult<DisplayLayoutSnapshot> after = Capture();
        if (applied != WIN32_ERROR.NO_ERROR)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, true, request.Persistent, (int)applied, "display_config_rejected",
                "SetDisplayConfig did not apply the layout.", before.Value, after.Success ? after.Value : null));
        }

        return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
            true, true, request.Persistent, null, null, null, before.Value, after.Success ? after.Value : null));
    }

    private static unsafe bool TryQuery(
        out DISPLAYCONFIG_PATH_INFO[] paths,
        out DISPLAYCONFIG_MODE_INFO[] modes,
        out uint pathCount,
        out string? error)
    {
        paths = [];
        modes = [];
        pathCount = 0;
        error = null;
        WIN32_ERROR size = PInvoke.GetDisplayConfigBufferSizes(
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS | QUERY_DISPLAY_CONFIG_FLAGS.QDC_VIRTUAL_MODE_AWARE,
            out pathCount,
            out uint modeCount);
        if (size != WIN32_ERROR.NO_ERROR || pathCount == 0)
        {
            error = "QueryDisplayConfig did not return an active topology.";
            return false;
        }

        paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        uint queriedPaths = pathCount;
        uint queriedModes = modeCount;
        WIN32_ERROR query = PInvoke.QueryDisplayConfig(
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS | QUERY_DISPLAY_CONFIG_FLAGS.QDC_VIRTUAL_MODE_AWARE,
            ref queriedPaths,
            paths,
            ref queriedModes,
            modes);
        pathCount = queriedPaths;

        if (query != WIN32_ERROR.NO_ERROR)
        {
            error = "QueryDisplayConfig failed (" + (int)query + ").";
            return false;
        }

        if (queriedModes < modes.Length)
        {
            Array.Resize(ref modes, (int)queriedModes);
        }

        return true;
    }

    private static unsafe bool TryDescribe(
        DISPLAYCONFIG_PATH_INFO path,
        DISPLAYCONFIG_MODE_INFO[] modes,
        IReadOnlyList<DisplayInfo> displays,
        out DisplayPathLayout? layout)
    {
        layout = null;
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source = default;
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        if (PInvoke.DisplayConfigGetDeviceInfo(ref Unsafe.As<DISPLAYCONFIG_SOURCE_DEVICE_NAME, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref source)) != 0)
        {
            return false;
        }

        string gdi = source.viewGdiDeviceName.AsReadOnlySpan().ToString().TrimEnd('\0');
        DISPLAYCONFIG_TARGET_DEVICE_NAME target = default;
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        string? devicePath = PInvoke.DisplayConfigGetDeviceInfo(ref Unsafe.As<DISPLAYCONFIG_TARGET_DEVICE_NAME, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref target)) == 0
            ? target.monitorDevicePath.AsReadOnlySpan().ToString().TrimEnd('\0')
            : null;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            devicePath = null;
        }

        int x = 0;
        int y = 0;
        int width = 0;
        int height = 0;
        uint sourceIndex = path.sourceInfo.modeInfoIdx;
        if (sourceIndex < modes.Length && modes[sourceIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            DISPLAYCONFIG_SOURCE_MODE sourceMode = modes[sourceIndex].sourceMode;
            x = sourceMode.position.x;
            y = sourceMode.position.y;
            width = checked((int)sourceMode.width);
            height = checked((int)sourceMode.height);
        }

        uint targetIndex = path.targetInfo.modeInfoIdx;
        if (targetIndex < modes.Length && modes[targetIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            DISPLAYCONFIG_2DREGION active = modes[targetIndex].targetMode.targetVideoSignalInfo.activeSize;
            width = width == 0 ? checked((int)active.cx) : width;
            height = height == 0 ? checked((int)active.cy) : height;
        }

        DisplayInfo? display = displays.FirstOrDefault(item => string.Equals(item.GdiDeviceName, gdi, StringComparison.OrdinalIgnoreCase));
        layout = new DisplayPathLayout(
            new DisplayTargetIdentity(
                devicePath,
                target.connectorInstance,
                DisplaySignalReader.Luid(path.targetInfo.adapterId.HighPart, path.targetInfo.adapterId.LowPart) ?? "00000000:00000000",
                path.targetInfo.id,
                path.sourceInfo.id),
            string.IsNullOrWhiteSpace(gdi) ? null : gdi,
            display?.Primary ?? false,
            x,
            y,
            width,
            height,
            path.targetInfo.refreshRate.Numerator,
            path.targetInfo.refreshRate.Denominator,
            DisplaySignalReader.TrimPrefix(path.targetInfo.rotation.ToString(), "DISPLAYCONFIG_ROTATION_") ?? "IDENTITY",
            DisplaySignalReader.TrimPrefix(path.targetInfo.scaling.ToString(), "DISPLAYCONFIG_SCALING_") ?? "IDENTITY",
            DesktopCoordinates.PhysicalScreenPixels);
        return width > 0 && height > 0;
    }

    private static bool TryPatch(ref DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes, DisplayTargetChange change)
    {
        bool patched = false;
        uint sourceIndex = path.sourceInfo.modeInfoIdx;
        if (sourceIndex < modes.Length && modes[sourceIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            DISPLAYCONFIG_MODE_INFO mode = modes[sourceIndex];
            DISPLAYCONFIG_SOURCE_MODE source = mode.sourceMode;
            source.position.x = change.X;
            source.position.y = change.Y;
            source.width = (uint)change.Width;
            source.height = (uint)change.Height;
            mode.sourceMode = source;
            modes[sourceIndex] = mode;
            patched = true;
        }

        uint targetIndex = path.targetInfo.modeInfoIdx;
        if (targetIndex < modes.Length && modes[targetIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            DISPLAYCONFIG_MODE_INFO mode = modes[targetIndex];
            DISPLAYCONFIG_TARGET_MODE target = mode.targetMode;
            target.targetVideoSignalInfo.activeSize.cx = (uint)change.Width;
            target.targetVideoSignalInfo.activeSize.cy = (uint)change.Height;
            if (change.RefreshNumerator is uint numerator && change.RefreshDenominator is uint denominator)
            {
                target.targetVideoSignalInfo.vSyncFreq.Numerator = numerator;
                target.targetVideoSignalInfo.vSyncFreq.Denominator = denominator;
                path.targetInfo.refreshRate.Numerator = numerator;
                path.targetInfo.refreshRate.Denominator = denominator;
            }

            mode.targetMode = target;
            modes[targetIndex] = mode;
            patched = true;
        }

        if (change.Rotation is not null
            && Enum.TryParse("DISPLAYCONFIG_ROTATION_" + change.Rotation, true, out DISPLAYCONFIG_ROTATION rotation))
        {
            path.targetInfo.rotation = rotation;
        }

        return patched;
    }

    private static unsafe WIN32_ERROR ApplyNative(
        uint pathCount,
        DISPLAYCONFIG_PATH_INFO[] paths,
        uint modeCount,
        DISPLAYCONFIG_MODE_INFO[] modes,
        SET_DISPLAY_CONFIG_FLAGS flags)
    {
        fixed (DISPLAYCONFIG_PATH_INFO* pathPointer = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modePointer = modes)
        {
            return (WIN32_ERROR)PInvoke.SetDisplayConfig(pathCount, pathPointer, modeCount, modePointer, flags);
        }
    }
}
