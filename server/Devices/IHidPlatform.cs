namespace Vestigo.Devices;

/// <summary>Reads HID capabilities for device interface paths supplied by the device enumerator.</summary>
public interface IHidPlatform
{
    /// <summary>The HID device-interface GUID from <c>HidD_GetHidGuid</c>.</summary>
    Guid InterfaceGuid { get; }

    /// <summary>Opens one interface read-only and returns capabilities. Does not read or write reports.</summary>
    HidInterfaceInspection Inspect(string devicePath);

    /// <summary>Opens one interface far enough to read its top-level usage.</summary>
    HidUsageMatch ReadUsage(string instanceId, string devicePath);
}
