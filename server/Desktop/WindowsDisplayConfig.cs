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

        if (!TryQuery(allPaths: true, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes, out uint pathCount, out string? error))
        {
            return OperationResult<DisplayLayoutSnapshot>.Fail("display_query_failed", error!);
        }

        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        List<DisplayPathLayout> layouts = [];
        for (int index = 0; index < pathCount; index++)
        {
            if (!TryDescribe(paths[index], modes, displays, includeInactive: true, out DisplayPathLayout? layout))
            {
                continue;
            }

            layouts.Add(layout!);
        }

        (string topology, IReadOnlyList<DisplayPathLayout> annotated) = DisplayLayoutPlanner.Annotate(layouts);
        return OperationResult<DisplayLayoutSnapshot>.Ok(DisplayLayoutPlanner.WithFingerprint(new DisplayLayoutSnapshot(
            DateTimeOffset.UtcNow,
            Process.GetCurrentProcess().SessionId,
            DesktopCoordinates.PhysicalScreenPixels,
            string.Empty,
            annotated,
            topology)));
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

        if (!TryQuery(allPaths: false, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes, out uint pathCount, out string? error))
        {
            return OperationResult<DisplayLayoutApplyResult>.Fail("display_query_failed", error!);
        }

        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        Dictionary<uint, (int X, int Y, uint Width, uint Height)> sharedSources = [];
        bool changed = false;
        bool[] seen = new bool[request.Targets.Count];
        for (int index = 0; index < pathCount; index++)
        {
            if (!TryDescribe(paths[index], modes, displays, includeInactive: false, out DisplayPathLayout? layout))
            {
                continue;
            }

            int targetIndex = -1;
            for (int candidate = 0; candidate < request.Targets.Count; candidate++)
            {
                if (DisplayLayoutPlanner.Matches(layout!.Identity, request.Targets[candidate]))
                {
                    targetIndex = candidate;
                    break;
                }
            }

            if (targetIndex < 0)
            {
                continue;
            }

            if (!TryPatch(ref paths[index], modes, layout!, request.Targets[targetIndex], sharedSources, out bool pathChanged, out string? patchError))
            {
                return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                    false, false, request.Persistent, null, "display_mode_unavailable",
                    patchError ?? "The active path has no source or target mode that can be updated.", before.Value, null));
            }

            seen[targetIndex] = true;
            changed |= pathChanged;
        }

        if (seen.Any(item => !item))
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, true, request.Persistent, null, "display_mode_unavailable",
                "A requested target was not in the active topology.", before.Value, null, "validate"));
        }

        if (!changed)
        {
            if (!request.Persistent)
            {
                return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                    true, true, request.Persistent, null, null, null, before.Value, before.Value, "unchanged", null, true));
            }

            SET_DISPLAY_CONFIG_FLAGS persistFlags = ApplicationFlags(persistent: true);
            WIN32_ERROR persisted = ApplyNative(pathCount, paths, (uint)modes.Length, modes, persistFlags);
            OperationResult<DisplayLayoutSnapshot> persistedAfter = Capture();
            if (persisted != WIN32_ERROR.NO_ERROR)
            {
                return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                    false, true, request.Persistent, (int)persisted, "display_config_rejected",
                    NativeFailure("apply", persisted, persistFlags), before.Value, persistedAfter.Success ? persistedAfter.Value : null, "apply", persistFlags.ToString()));
            }

            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                true, true, request.Persistent, null, null, null, before.Value,
                persistedAfter.Success ? persistedAfter.Value : null, "unchanged", persistFlags.ToString(), true));
        }

        SET_DISPLAY_CONFIG_FLAGS validateFlags = ValidationFlags();
        WIN32_ERROR validated = ApplyNative(pathCount, paths, (uint)modes.Length, modes, validateFlags);
        if (validated != WIN32_ERROR.NO_ERROR)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, false, request.Persistent, (int)validated, "display_config_rejected",
                NativeFailure("validate", validated, validateFlags), before.Value, null, "validate", validateFlags.ToString()));
        }

        if (request.ValidateOnly)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, true, request.Persistent, null, null, null, before.Value, before.Value));
        }

        // SDC_VALIDATE and SDC_APPLY together are an invalid combination and SetDisplayConfig returns 87.
        SET_DISPLAY_CONFIG_FLAGS applyFlags = ApplicationFlags(request.Persistent);

        WIN32_ERROR applied = ApplyNative(pathCount, paths, (uint)modes.Length, modes, applyFlags);
        OperationResult<DisplayLayoutSnapshot> after = Capture();
        if (applied != WIN32_ERROR.NO_ERROR)
        {
            return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
                false, true, request.Persistent, (int)applied, "display_config_rejected",
                NativeFailure("apply", applied, applyFlags), before.Value, after.Success ? after.Value : null, "apply", applyFlags.ToString()));
        }

        return OperationResult<DisplayLayoutApplyResult>.Ok(new DisplayLayoutApplyResult(
            true, true, request.Persistent, null, null, null, before.Value, after.Success ? after.Value : null));
    }

    private static unsafe bool TryQuery(
        bool allPaths,
        out DISPLAYCONFIG_PATH_INFO[] paths,
        out DISPLAYCONFIG_MODE_INFO[] modes,
        out uint pathCount,
        out string? error)
    {
        paths = [];
        modes = [];
        pathCount = 0;
        error = null;
        QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_VIRTUAL_MODE_AWARE
            | (allPaths ? QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS : QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS);
        WIN32_ERROR size = PInvoke.GetDisplayConfigBufferSizes(flags, out pathCount, out uint modeCount);
        if (size != WIN32_ERROR.NO_ERROR || (!allPaths && pathCount == 0))
        {
            error = "QueryDisplayConfig did not return an active topology.";
            return false;
        }

        if (pathCount == 0)
        {
            return true;
        }

        paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];
        uint queriedPaths = pathCount;
        uint queriedModes = modeCount;
        WIN32_ERROR query = PInvoke.QueryDisplayConfig(
            flags,
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
        bool includeInactive,
        out DisplayPathLayout? layout)
    {
        layout = null;
        string gdi = string.Empty;
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source = default;
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        if (PInvoke.DisplayConfigGetDeviceInfo(ref Unsafe.As<DISPLAYCONFIG_SOURCE_DEVICE_NAME, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref source)) == 0)
        {
            gdi = source.viewGdiDeviceName.AsReadOnlySpan().ToString().TrimEnd('\0');
        }
        else if (!includeInactive)
        {
            return false;
        }

        DISPLAYCONFIG_TARGET_DEVICE_NAME target = default;
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        bool named = PInvoke.DisplayConfigGetDeviceInfo(ref Unsafe.As<DISPLAYCONFIG_TARGET_DEVICE_NAME, DISPLAYCONFIG_DEVICE_INFO_HEADER>(ref target)) == 0;
        string? devicePath = named ? target.monitorDevicePath.AsReadOnlySpan().ToString().TrimEnd('\0') : null;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            devicePath = null;
        }

        int x = 0;
        int y = 0;
        int width = 0;
        int height = 0;
        int? desktopWidth = null;
        int? desktopHeight = null;
        int? sourceModeIndex = TrySourceModeIndex(path, out uint sourceIndex) ? checked((int)sourceIndex) : null;
        if (sourceModeIndex is int sourceMode
            && (uint)sourceMode < modes.Length
            && modes[sourceMode].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            DISPLAYCONFIG_SOURCE_MODE sourceModeInfo = modes[sourceMode].sourceMode;
            x = sourceModeInfo.position.x;
            y = sourceModeInfo.position.y;
            width = checked((int)sourceModeInfo.width);
            height = checked((int)sourceModeInfo.height);
            desktopWidth = width;
            desktopHeight = height;
        }

        int? signalWidth = null;
        int? signalHeight = null;
        uint? signalNumerator = null;
        uint? signalDenominator = null;
        int? targetModeIndex = TryTargetModeIndex(path, out uint targetIndex) ? checked((int)targetIndex) : null;
        if (targetModeIndex is int targetMode
            && (uint)targetMode < modes.Length
            && modes[targetMode].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            DISPLAYCONFIG_VIDEO_SIGNAL_INFO signal = modes[targetMode].targetMode.targetVideoSignalInfo;
            signalWidth = DisplaySignalReader.Count(signal.activeSize.cx);
            signalHeight = DisplaySignalReader.Count(signal.activeSize.cy);
            signalNumerator = signal.vSyncFreq.Numerator;
            signalDenominator = signal.vSyncFreq.Denominator;
            width = width == 0 && signalWidth is int signalW ? signalW : width;
            height = height == 0 && signalHeight is int signalH ? signalH : height;
        }

        uint flags = path.flags;
        DisplayInfo? display = displays.FirstOrDefault(item => string.Equals(item.GdiDeviceName, gdi, StringComparison.OrdinalIgnoreCase));
        layout = new DisplayPathLayout(
            new DisplayTargetIdentity(
                devicePath,
                named ? target.connectorInstance : 0,
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
            DesktopCoordinates.PhysicalScreenPixels,
            (flags & 0x1) != 0,
            flags,
            DisplayLayoutPlanner.DescribePathFlags(flags),
            sourceModeIndex,
            targetModeIndex,
            desktopWidth,
            desktopHeight,
            signalWidth,
            signalHeight,
            signalNumerator,
            signalDenominator,
            null,
            named && target.flags.edidIdsValid ? DisplayTopology.DecodeManufacturer(target.edidManufactureId) : null,
            named && target.flags.edidIdsValid && target.edidProductCodeId != 0 ? "0x" + target.edidProductCodeId.ToString("X4") : null,
            named ? target.flags.edidIdsValid : null,
            named ? target.flags.friendlyNameFromEdid : null,
            named ? target.flags.friendlyNameForced : null,
            named ? target.flags.reserved : null,
            named ? NullIfEmpty(target.monitorFriendlyDeviceName.AsReadOnlySpan().ToString().TrimEnd('\0')) : null);
        return includeInactive || (width > 0 && height > 0);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Validate and apply are separate calls. Combining the two flags is ERROR_INVALID_PARAMETER.</summary>
    public static bool SeparatesValidationFromApply(bool persistent) =>
        !ValidationFlags().HasFlag(SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY)
        && !ApplicationFlags(persistent).HasFlag(SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE)
        && ApplicationFlags(persistent).HasFlag(SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY)
        && ApplicationFlags(persistent).HasFlag(SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE) == persistent;

    private static SET_DISPLAY_CONFIG_FLAGS ValidationFlags() =>
        SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE | SuppliedFlags;

    private static SET_DISPLAY_CONFIG_FLAGS ApplicationFlags(bool persistent)
    {
        SET_DISPLAY_CONFIG_FLAGS flags = SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY | SuppliedFlags;
        return persistent ? flags | SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE : flags;
    }

    private static SET_DISPLAY_CONFIG_FLAGS SuppliedFlags =>
        SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG
        | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES
        | SET_DISPLAY_CONFIG_FLAGS.SDC_VIRTUAL_MODE_AWARE;

    private static string NativeFailure(string stage, WIN32_ERROR error, SET_DISPLAY_CONFIG_FLAGS flags) =>
        $"SetDisplayConfig {stage} failed. Stage={stage}. NativeError={(int)error}. Flags={flags}.";

    private static bool TryPatch(
        ref DISPLAYCONFIG_PATH_INFO path,
        DISPLAYCONFIG_MODE_INFO[] modes,
        DisplayPathLayout current,
        DisplayTargetChange change,
        Dictionary<uint, (int X, int Y, uint Width, uint Height)> sharedSources,
        out bool changed,
        out string? error)
    {
        changed = false;
        error = null;
        DisplayModePatch plan = DisplayModePatch.For(current, change);
        if (plan.Unsupported)
        {
            error = "The desktop size and signal size differ, so the target signal was left unchanged.";
            return false;
        }

        if (!plan.AnyChange)
        {
            return true;
        }

        if (!TrySourceModeIndex(path, out uint sourceIndex)
            || sourceIndex >= modes.Length
            || modes[sourceIndex].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            error = "The active path has no source mode that can be updated.";
            return false;
        }

        DISPLAYCONFIG_MODE_INFO sourceMode = modes[sourceIndex];
        DISPLAYCONFIG_SOURCE_MODE source = sourceMode.sourceMode;
        int nextX = plan.Move ? change.X : source.position.x;
        int nextY = plan.Move ? change.Y : source.position.y;
        uint nextWidth = plan.ResizeSource ? (uint)change.Width : source.width;
        uint nextHeight = plan.ResizeSource ? (uint)change.Height : source.height;
        if (sharedSources.TryGetValue(sourceIndex, out (int X, int Y, uint Width, uint Height) previous)
            && (previous.X != nextX || previous.Y != nextY || previous.Width != nextWidth || previous.Height != nextHeight))
        {
            error = "Clone paths share a source mode and requested different desktop geometry.";
            return false;
        }

        source.position.x = nextX;
        source.position.y = nextY;
        source.width = nextWidth;
        source.height = nextHeight;
        sourceMode.sourceMode = source;
        modes[sourceIndex] = sourceMode;
        sharedSources[sourceIndex] = (nextX, nextY, nextWidth, nextHeight);

        if (plan.ResizeSignal || plan.ChangeRefresh)
        {
            if (!TryTargetModeIndex(path, out uint targetIndex)
                || targetIndex >= modes.Length
                || modes[targetIndex].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
            {
                error = "The active path has no target mode that can be updated.";
                return false;
            }

            DISPLAYCONFIG_MODE_INFO targetMode = modes[targetIndex];
            DISPLAYCONFIG_TARGET_MODE target = targetMode.targetMode;
            if (plan.ResizeSignal)
            {
                target.targetVideoSignalInfo.activeSize.cx = (uint)change.Width;
                target.targetVideoSignalInfo.activeSize.cy = (uint)change.Height;
            }

            if (plan.ChangeRefresh && change.RefreshNumerator is uint numerator && change.RefreshDenominator is uint denominator)
            {
                target.targetVideoSignalInfo.vSyncFreq.Numerator = numerator;
                target.targetVideoSignalInfo.vSyncFreq.Denominator = denominator;
                path.targetInfo.refreshRate.Numerator = numerator;
                path.targetInfo.refreshRate.Denominator = denominator;
            }

            targetMode.targetMode = target;
            modes[targetIndex] = targetMode;
        }

        if (plan.ChangeRotation
            && Enum.TryParse("DISPLAYCONFIG_ROTATION_" + change.Rotation, true, out DISPLAYCONFIG_ROTATION rotation))
        {
            path.targetInfo.rotation = rotation;
        }

        changed = true;
        return true;
    }

    private static bool TrySourceModeIndex(DISPLAYCONFIG_PATH_INFO path, out uint index)
    {
        if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE) != 0)
        {
            index = path.sourceInfo.sourceModeInfoIdx;
            return index != PInvoke.DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID;
        }

        index = path.sourceInfo.modeInfoIdx;
        return index != PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
    }

    private static bool TryTargetModeIndex(DISPLAYCONFIG_PATH_INFO path, out uint index)
    {
        if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE) != 0)
        {
            index = path.targetInfo.targetModeInfoIdx;
            return index != PInvoke.DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID;
        }

        index = path.targetInfo.modeInfoIdx;
        return index != PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
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
