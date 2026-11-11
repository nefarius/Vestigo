using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Abstractions;
using Vestigo.Devices;

namespace Vestigo.Tools;

/// <summary>Read-only HID capability tools. They do not read reports or write features.</summary>
[McpServerToolType]
public sealed class HidTools(HidService hid)
{
    [McpServerTool(Name = "inspect_hid_device")]
    [Description("Reads HID capabilities for one device instance: vendor, product, version, manufacturer, product and serial strings, top-level usage, report lengths, bounded button and value usages, and link collections. A closed interface reports openError and leaves capabilities null. Does not return the raw report descriptor, input reports, or the process holding an exclusive handle.")]
    public OperationResult<HidInspection> InspectHidDevice(
        [Description("Device instance ID, for example USB\\VID_046D&PID_C52B\\1.")] string instanceId) =>
        hid.Inspect(instanceId);

    [McpServerTool(Name = "list_hid_usages")]
    [Description("Lists the top-level HID usage page and usage of present HID interfaces, at most 128. Pass usagePage to keep only that page. An interface that cannot be opened has null usage fields and openError. Does not read reports.")]
    public OperationResult<HidUsageList> ListHidUsages(
        [Description("Optional usage page from 0 through 65535. Omit it to return every scanned interface.")] int? usagePage = null) =>
        hid.ListUsages(usagePage);
}
