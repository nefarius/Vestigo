using System.Text;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Devices;

/// <summary>
///     HID capability reads through CsWin32. Opens with read access, then with no access if the first open is denied.
///     Never reads input reports or sends feature reports.
/// </summary>
internal sealed class WindowsHidPlatform : IHidPlatform
{
    private Guid? _interfaceGuid;

    public Guid InterfaceGuid
    {
        get
        {
            RequireWindows();
            if (_interfaceGuid is null)
            {
                PInvoke.HidD_GetHidGuid(out Guid guid);
                _interfaceGuid = guid;
            }

            return _interfaceGuid.Value;
        }
    }

    public HidInterfaceInspection Inspect(string devicePath)
    {
        RequireWindows();
        if (!TryOpen(devicePath, out SafeFileHandle? handle, out string? access, out string? error))
        {
            return Closed(devicePath, error);
        }

        using (handle)
        {
            HidIdentity identity = ReadIdentity(handle!);
            if (!TryPreparsed(handle!, out HidD_FreePreparsedDataSafeHandle? preparsed))
            {
                return Opened(devicePath, access, identity, null, [], false, [], false, [], false, detailsIncomplete: true);
            }

            using (preparsed)
            {
                using var lease = preparsed!.Lease();
                return ReadPreparsed(devicePath, access, identity, lease.Value);
            }
        }
    }

    public HidUsageMatch ReadUsage(string instanceId, string devicePath)
    {
        RequireWindows();
        if (!TryOpen(devicePath, out SafeFileHandle? handle, out _, out string? error))
        {
            return new HidUsageMatch(instanceId, devicePath, false, error, null, null);
        }

        using (handle)
        {
            if (!TryPreparsed(handle!, out HidD_FreePreparsedDataSafeHandle? preparsed))
            {
                return new HidUsageMatch(instanceId, devicePath, true, null, null, null);
            }

            using (preparsed)
            {
                using var lease = preparsed!.Lease();
                if ((int)PInvoke.HidP_GetCaps(lease.Value, out HIDP_CAPS caps) != HidProjection.StatusSuccess)
                {
                    return new HidUsageMatch(instanceId, devicePath, true, null, null, null);
                }

                return new HidUsageMatch(instanceId, devicePath, true, null, caps.UsagePage, caps.Usage);
            }
        }
    }

    private static HidInterfaceInspection ReadPreparsed(
        string devicePath,
        string? access,
        HidIdentity identity,
        PHIDP_PREPARSED_DATA preparsed)
    {
        if ((int)PInvoke.HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HidProjection.StatusSuccess)
        {
            return Opened(devicePath, access, identity, null, [], false, [], false, [], false, detailsIncomplete: true);
        }

        bool incomplete = false;
        List<HidControl> buttons = [];
        bool buttonsTruncated = false;
        List<HidControl> values = [];
        bool valuesTruncated = false;
        foreach ((HIDP_REPORT_TYPE type, string kind) in ReportKinds())
        {
            incomplete |= !TryButtons(preparsed, type, kind, buttons, ref buttonsTruncated);
            incomplete |= !TryValues(preparsed, type, kind, values, ref valuesTruncated);
        }

        bool collectionsTruncated = false;
        List<HidCollection> collections = [];
        incomplete |= !TryCollections(preparsed, caps.NumberLinkCollectionNodes, collections, ref collectionsTruncated);
        return Opened(
            devicePath,
            access,
            identity,
            HidProjection.Capabilities(caps),
            buttons,
            buttonsTruncated,
            values,
            valuesTruncated,
            collections,
            collectionsTruncated,
            incomplete);
    }

    private static bool TryButtons(
        PHIDP_PREPARSED_DATA preparsed,
        HIDP_REPORT_TYPE type,
        string kind,
        List<HidControl> target,
        ref bool truncated)
    {
        HIDP_BUTTON_CAPS[] buffer = new HIDP_BUTTON_CAPS[HidProjection.MaxReturnedControls];
        ushort length = HidProjection.MaxReturnedControls;
        int status = (int)PInvoke.HidP_GetButtonCaps(type, buffer, ref length, preparsed);
        if (!Accepted(status, length, buffer.Length, ref truncated, out int count))
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            target.Add(HidProjection.Button(kind, buffer[index]));
        }

        return true;
    }

    private static bool TryValues(
        PHIDP_PREPARSED_DATA preparsed,
        HIDP_REPORT_TYPE type,
        string kind,
        List<HidControl> target,
        ref bool truncated)
    {
        HIDP_VALUE_CAPS[] buffer = new HIDP_VALUE_CAPS[HidProjection.MaxReturnedControls];
        ushort length = HidProjection.MaxReturnedControls;
        int status = (int)PInvoke.HidP_GetValueCaps(type, buffer, ref length, preparsed);
        if (!Accepted(status, length, buffer.Length, ref truncated, out int count))
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            target.Add(HidProjection.Value(kind, buffer[index]));
        }

        return true;
    }

    private static bool TryCollections(
        PHIDP_PREPARSED_DATA preparsed,
        int reported,
        List<HidCollection> target,
        ref bool truncated)
    {
        if (reported <= 0)
        {
            return true;
        }

        if (reported > HidProjection.MaxAcceptedCount)
        {
            truncated = true;
            return false;
        }

        int capacity = Math.Min(reported, HidProjection.MaxReturnedCollections);
        HIDP_LINK_COLLECTION_NODE[] buffer = new HIDP_LINK_COLLECTION_NODE[capacity];
        uint length = (uint)capacity;
        int status = (int)PInvoke.HidP_GetLinkCollectionNodes(buffer, ref length, preparsed);
        if (status == HidProjection.StatusUsageNotFound)
        {
            return true;
        }

        if (status != HidProjection.StatusSuccess && status != HidProjection.StatusBufferTooSmall)
        {
            return false;
        }

        int count = (int)Math.Min(length, (uint)buffer.Length);
        for (int index = 0; index < count; index++)
        {
            target.Add(HidProjection.Collection(index, buffer[index]));
        }

        truncated = status == HidProjection.StatusBufferTooSmall || reported > count;
        return true;
    }

    private static bool Accepted(int status, int length, int capacity, ref bool truncated, out int count)
    {
        if (status == HidProjection.StatusUsageNotFound)
        {
            count = 0;
            return true;
        }

        if (status != HidProjection.StatusSuccess && status != HidProjection.StatusBufferTooSmall)
        {
            count = 0;
            return false;
        }

        count = Math.Clamp(length, 0, capacity);
        truncated |= status == HidProjection.StatusBufferTooSmall;
        return true;
    }

    private static unsafe HidIdentity ReadIdentity(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            HANDLE native = (HANDLE)handle.DangerousGetHandle();
            HIDD_ATTRIBUTES attributes = new() { Size = (uint)sizeof(HIDD_ATTRIBUTES) };
            bool haveAttributes = PInvoke.HidD_GetAttributes(native, &attributes);
            return new HidIdentity(
                haveAttributes ? attributes.VendorID : null,
                haveAttributes ? attributes.ProductID : null,
                haveAttributes ? attributes.VersionNumber : null,
                ReadString(handle, PInvoke.HidD_GetManufacturerString),
                ReadString(handle, PInvoke.HidD_GetProductString),
                ReadString(handle, PInvoke.HidD_GetSerialNumberString));
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static bool TryPreparsed(SafeFileHandle handle, out HidD_FreePreparsedDataSafeHandle? preparsed)
    {
        if (PInvoke.HidD_GetPreparsedData(handle, out HidD_FreePreparsedDataSafeHandle created))
        {
            preparsed = created;
            return true;
        }

        created.SetHandleAsInvalid();
        created.Dispose();
        preparsed = null;
        return false;
    }

    private static string? ReadString(SafeFileHandle handle, Func<SafeFileHandle, Span<byte>, BOOLEAN> read)
    {
        Span<byte> buffer = stackalloc byte[(HidProjection.MaxTextChars + 1) * 2];
        if (!read(handle, buffer))
        {
            return null;
        }

        int end = 0;
        while (end + 1 < buffer.Length && (buffer[end] != 0 || buffer[end + 1] != 0))
        {
            end += 2;
        }

        return HidProjection.BoundText(Encoding.Unicode.GetString(buffer[..end]));
    }

    private static bool TryOpen(string devicePath, out SafeFileHandle? handle, out string? access, out string? error)
    {
        handle = Open(devicePath, (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ);
        if (handle is not null)
        {
            access = "read";
            error = null;
            return true;
        }

        int first = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (first is not (int)WIN32_ERROR.ERROR_ACCESS_DENIED and not (int)WIN32_ERROR.ERROR_SHARING_VIOLATION)
        {
            access = null;
            error = OpenError(first);
            return false;
        }

        handle = Open(devicePath, 0);
        if (handle is not null)
        {
            access = "query";
            error = null;
            return true;
        }

        access = null;
        error = OpenError(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return false;
    }

    private static SafeFileHandle? Open(string devicePath, uint access)
    {
        SafeFileHandle handle = PInvoke.CreateFile(
            devicePath,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            hTemplateFile: null);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    private static string OpenError(int error) => error switch
    {
        (int)WIN32_ERROR.ERROR_ACCESS_DENIED => "access_denied",
        (int)WIN32_ERROR.ERROR_SHARING_VIOLATION => "sharing_violation",
        (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND or (int)WIN32_ERROR.ERROR_PATH_NOT_FOUND => "not_found",
        _ => "open_failed",
    };

    private static HidInterfaceInspection Closed(string path, string? error) =>
        new(path, false, error, null, null, null, [], false, [], false, [], false, false);

    private static HidInterfaceInspection Opened(
        string path,
        string? access,
        HidIdentity identity,
        HidCapabilities? capabilities,
        IReadOnlyList<HidControl> buttons,
        bool buttonsTruncated,
        IReadOnlyList<HidControl> values,
        bool valuesTruncated,
        IReadOnlyList<HidCollection> collections,
        bool collectionsTruncated,
        bool detailsIncomplete) =>
        new(path, true, null, access, identity, capabilities, buttons, buttonsTruncated, values, valuesTruncated, collections, collectionsTruncated, detailsIncomplete);

    private static IEnumerable<(HIDP_REPORT_TYPE Type, string Kind)> ReportKinds()
    {
        yield return (HIDP_REPORT_TYPE.HidP_Input, "input");
        yield return (HIDP_REPORT_TYPE.HidP_Output, "output");
        yield return (HIDP_REPORT_TYPE.HidP_Feature, "feature");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DeviceControlException(DeviceControlException.WindowsRequired, "HID capabilities require Windows.");
        }
    }
}
