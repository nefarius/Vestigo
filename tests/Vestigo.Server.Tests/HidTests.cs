using Vestigo.Devices;
using Vestigo.Tools;

using Windows.Win32.Devices.HumanInterfaceDevice;

namespace Vestigo.Server.Tests;

public sealed class HidTests
{
    [Fact]
    public void Projection_KeepsASingleUsageAndARangeApart()
    {
        HIDP_BUTTON_CAPS single = new()
        {
            UsagePage = 0x09,
            ReportID = 1,
            IsRange = false,
            IsAbsolute = true,
            LinkCollection = 2,
            ReportCount = 1,
        };
        single.NotRange.Usage = 0x04;
        HidControl button = HidProjection.Button("input", single);
        Assert.Equal("button", button.Kind);
        Assert.Equal(0x04, button.Usage);
        Assert.Null(button.UsageMin);
        Assert.Null(button.LogicalMin);
        Assert.True(button.Absolute);

        HIDP_VALUE_CAPS range = new()
        {
            UsagePage = 0x01,
            ReportID = 3,
            IsRange = true,
            IsAbsolute = true,
            HasNull = true,
            LinkCollection = 4,
            LogicalMin = 0,
            LogicalMax = 100,
            PhysicalMin = 0,
            PhysicalMax = 255,
            BitSize = 8,
            ReportCount = 2,
            Units = 0x11,
            UnitsExp = 0x0E,
        };
        range.Range.UsageMin = 0x30;
        range.Range.UsageMax = 0x31;
        HidControl value = HidProjection.Value("input", range);
        Assert.Null(value.Usage);
        Assert.Equal(0x30, value.UsageMin);
        Assert.Equal(0x31, value.UsageMax);
        Assert.Equal(100, value.LogicalMax);
        Assert.Equal(true, value.HasNull);
        Assert.Equal(0x11, value.Units);
    }

    [Fact]
    public void Projection_NamesOnlyTheSevenCollectionTypes()
    {
        HIDP_LINK_COLLECTION_NODE node = new()
        {
            LinkUsagePage = 1,
            LinkUsage = 2,
            Parent = 0,
            NumberOfChildren = 3,
            NextSibling = 0,
            FirstChild = 1,
        };
        node.CollectionType = 1;
        node.IsAlias = true;
        HidCollection named = HidProjection.Collection(0, node);
        Assert.Equal("application", named.CollectionTypeName);
        Assert.True(named.Alias);

        node.CollectionType = 9;
        node.IsAlias = false;
        Assert.Null(HidProjection.Collection(1, node).CollectionTypeName);
        Assert.Equal(126, HidProjection.BoundText(new string('a', 200))!.Length);
        Assert.Null(HidProjection.BoundText("  "));
        Assert.Equal(0x00110000, HidProjection.StatusSuccess);
        Assert.Equal(unchecked((int)0xC0110007), HidProjection.StatusBufferTooSmall);
        Assert.Equal(unchecked((int)0xC0110004), HidProjection.StatusUsageNotFound);
    }

    [Fact]
    public void Inspect_CapsInterfacesAndDoesNotInventCapabilitiesForAClosedOne()
    {
        FakeDevices devices = new();
        devices.Add("USB\\VID_1\\A");
        for (int index = 0; index < HidService.MaxInterfaces + 1; index++)
        {
            devices.Interfaces.Add(new DeviceInterfaceMatch("USB\\VID_1\\A", "\\\\?\\hid#" + index));
        }

        devices.Interfaces.Add(new DeviceInterfaceMatch("USB\\VID_1\\B", "\\\\?\\other"));
        FakeHid hid = new();
        hid.Closed.Add("\\\\?\\hid#0");
        var result = new HidService(devices, hid).Inspect("usb\\vid_1\\a");

        Assert.True(result.Success);
        Assert.Equal("USB\\VID_1\\A", result.Value!.InstanceId, ignoreCase: true);
        Assert.True(result.Value.Truncated);
        Assert.Equal(HidService.MaxInterfaces, result.Value.Interfaces.Count);
        HidInterfaceInspection closed = result.Value.Interfaces[0];
        Assert.False(closed.Opened);
        Assert.Equal("sharing_violation", closed.OpenError);
        Assert.Null(closed.Capabilities);
        Assert.Empty(closed.Buttons);
        Assert.NotNull(result.Value.Interfaces[1].Capabilities);
        Assert.DoesNotContain(result.Value.Interfaces, item => item.Path == "\\\\?\\other");
    }

    [Fact]
    public void ListUsages_FiltersByPageAndKeepsAClosedInterfaceWhenUnfiltered()
    {
        FakeDevices devices = new();
        devices.Interfaces.Add(new DeviceInterfaceMatch("USB\\VID_1\\A", "\\\\?\\keyboard"));
        devices.Interfaces.Add(new DeviceInterfaceMatch("USB\\VID_1\\B", "\\\\?\\mouse"));
        devices.Interfaces.Add(new DeviceInterfaceMatch("USB\\VID_1\\C", "\\\\?\\closed"));
        FakeHid hid = new();
        hid.Pages["\\\\?\\keyboard"] = 1;
        hid.Pages["\\\\?\\mouse"] = 1;
        hid.Usages["\\\\?\\mouse"] = 2;
        hid.Closed.Add("\\\\?\\closed");
        HidService service = new(devices, hid);

        var filtered = service.ListUsages(1);
        Assert.Equal(2, filtered.Value!.Devices.Count);
        Assert.Equal(2, filtered.Value.Devices[1].Usage);
        Assert.False(filtered.Value.Truncated);

        var all = service.ListUsages(null);
        Assert.Equal(3, all.Value!.Devices.Count);
        Assert.Null(all.Value.Devices[2].UsagePage);
        Assert.Equal("sharing_violation", all.Value.Devices[2].OpenError);
        Assert.Equal("invalid_request", service.ListUsages(-1).ErrorCode);
        Assert.Equal("invalid_instance_id", service.Inspect("noseparator").ErrorCode);
        Assert.Equal("device_not_found", service.Inspect("USB\\MISSING\\1").ErrorCode);
    }

    [Fact]
    public void WindowsPlatform_ReadsAPresentHidInterfaceWhenOneExists()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "HID capability reads require Windows.");
        WindowsHidPlatform platform = new();
        IReadOnlyList<DeviceInterfaceMatch> found = new DeviceControl().FindByInterface(platform.InterfaceGuid, presentOnly: true, maxResults: 1);
        Assert.SkipUnless(found.Count > 0, "This machine has no present HID interface.");

        HidInterfaceInspection inspection = platform.Inspect(found[0].DevicePath);
        Assert.Equal(found[0].DevicePath, inspection.Path);
        if (!inspection.Opened)
        {
            Assert.Null(inspection.Capabilities);
            Assert.Contains(inspection.OpenError, new[] { "access_denied", "sharing_violation", "not_found", "open_failed" });
            return;
        }

        Assert.Null(inspection.OpenError);
        Assert.True(inspection.Identity?.Manufacturer is null || inspection.Identity.Manufacturer.Length <= HidProjection.MaxTextChars);
        Assert.True(inspection.Identity?.Product is null || inspection.Identity.Product.Length <= HidProjection.MaxTextChars);
        Assert.True(inspection.Identity?.SerialNumber is null || inspection.Identity.SerialNumber.Length <= HidProjection.MaxTextChars);
        if (inspection.Capabilities is null)
        {
            Assert.True(inspection.DetailsIncomplete);
        }
        else
        {
            Assert.InRange(inspection.Capabilities.UsagePage, 0, ushort.MaxValue);
        }

        Assert.All(inspection.Buttons, button => Assert.Equal("button", button.Kind));
        Assert.All(inspection.Values, value => Assert.Equal("value", value.Kind));
        Assert.True(inspection.Buttons.Count <= HidProjection.MaxReturnedControls * 3);
        Assert.True(inspection.Collections.Count <= HidProjection.MaxReturnedCollections);
    }

    private sealed class FakeDevices : IDeviceControl
    {
        public List<DeviceInterfaceMatch> Interfaces { get; } = [];

        private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string instanceId) => _instances.Add(instanceId);

        public DeviceDetails GetDevice(string instanceId)
        {
            if (!_instances.Contains(instanceId))
            {
                throw new DeviceControlException(DeviceControlException.DeviceNotFound, "The device was not found.");
            }

            return new DeviceDetails(instanceId, null, [], [], null, null, null, null, null, null, null, null, null, null, null, null, null);
        }

        public IReadOnlyList<DeviceInterfaceMatch> FindByInterface(Guid interfaceGuid, bool presentOnly, int maxResults) =>
            Interfaces.Take(maxResults).ToList();

        public void Restart(string instanceId) => throw new NotSupportedException();

        public void Enable(string instanceId) => throw new NotSupportedException();

        public void Disable(string instanceId) => throw new NotSupportedException();

        public IReadOnlyList<string> FindByHardwareId(Guid classGuid, string hardwareId, bool presentOnly, bool allowPartial) =>
            throw new NotSupportedException();

        public IReadOnlyList<DeviceEntry> EnumerateDevices(bool presentOnly) => throw new NotSupportedException();

        public ClassFilterSet GetClassFilters(Guid classGuid) => throw new NotSupportedException();

        public IReadOnlyList<DriverStoreEntry> GetDriverStore(bool includeInbox) => throw new NotSupportedException();
    }

    private sealed class FakeHid : IHidPlatform
    {
        public Guid InterfaceGuid { get; } = Guid.Parse("4d1e55b2-f16f-11cf-88cb-001111000030");

        public HashSet<string> Closed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> Usages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HidInterfaceInspection Inspect(string devicePath)
        {
            if (Closed.Contains(devicePath))
            {
                return new HidInterfaceInspection(devicePath, false, "sharing_violation", null, null, null, [], false, [], false, [], false, false);
            }

            HidCapabilities capabilities = new(Pages.GetValueOrDefault(devicePath, 1), Usages.GetValueOrDefault(devicePath, 6), 8, 0, 0, 1, 1, 0, 0, 0, 0, 0);
            return new HidInterfaceInspection(
                devicePath,
                true,
                null,
                "read",
                new HidIdentity(1, 2, 3, "Contoso", "Pad", null),
                capabilities,
                [],
                false,
                [],
                false,
                [],
                false,
                false);
        }

        public HidUsageMatch ReadUsage(string instanceId, string devicePath)
        {
            if (Closed.Contains(devicePath))
            {
                return new HidUsageMatch(instanceId, devicePath, false, "sharing_violation", null, null);
            }

            return new HidUsageMatch(instanceId, devicePath, true, null, Pages.GetValueOrDefault(devicePath, 1), Usages.GetValueOrDefault(devicePath, 6));
        }
    }
}
