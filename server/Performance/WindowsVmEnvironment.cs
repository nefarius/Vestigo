using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.System.SystemInformation;

using WmiLight;

namespace Vestigo.Performance;

/// <summary>Inspects hypervisor presence, vCPU topology, paravirt devices, timer resolution, and VBS/HVCI.</summary>
public sealed class WindowsVmEnvironment : IVmEnvironmentSource
{
    public OperationResult<VmEnvironment> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<VmEnvironment>.Fail("windows_required", "VM environment inspection requires Windows.");
        }

        try
        {
            IReadOnlyList<WmiObject> computers = WmiQuery.Query(
                @"root\CIMV2",
                "SELECT Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem");
            IReadOnlyList<WmiObject> bios = WmiQuery.Query(
                @"root\CIMV2",
                "SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS");
            IReadOnlyList<WmiObject> services = WmiQuery.Query(
                @"root\CIMV2",
                "SELECT Name, State, PathName FROM Win32_Service");
            IReadOnlyList<WmiObject> pnp = WmiQuery.Query(
                @"root\CIMV2",
                "SELECT Name, Service, PNPClass, Manufacturer FROM Win32_PnPEntity");
            IReadOnlyList<WmiObject> guard = WmiQuery.Query(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, HypervisorEnforcedCodeIntegrityStatus FROM Win32_DeviceGuard");

            WmiObject? computer = computers.FirstOrDefault();
            WmiObject? biosRow = bios.FirstOrDefault();
            bool hypervisor = (computer is not null && WmiQuery.Boolean(computer, "HypervisorPresent") == true)
                              || LooksVirtual(WmiQuery.String(computer, "Manufacturer"), WmiQuery.String(computer, "Model"));
            string? vendor = InferVendor(
                WmiQuery.String(computer, "Manufacturer"),
                WmiQuery.String(computer, "Model"),
                WmiQuery.String(biosRow, "Manufacturer"));

            List<ParavirtDevice> devices =
            [
                MatchDevice("storage", pnp, services, ["vioscsi", "viostor", "storvsc", "pvscsi", "vmscsi", "VBoxSCSI"]),
                MatchDevice("network", pnp, services, ["netvsc", "netkvm", "vmxnet", "VBoxNet"]),
                MatchDevice("balloon", pnp, services, ["balloon", "vioballoon", "blnsvr", "dm-agent", "vmmemctl"])
            ];

            TimerResolution? timer = null;
            if (NativeNtQuery.NtQueryTimerResolution(out uint maximum, out uint minimum, out uint current) == 0)
            {
                timer = new TimerResolution(current, minimum, maximum);
            }

            bool? hvci = null;
            bool? vbs = null;
            if (guard.Count > 0)
            {
                vbs = WmiQuery.Int32(guard[0], "VirtualizationBasedSecurityStatus") is > 0;
                hvci = WmiQuery.Int32(guard[0], "HypervisorEnforcedCodeIntegrityStatus") is > 0;
            }

            List<string> notes = [];
            if (hypervisor && devices.All(device => !device.Present || device.Kind != "storage" || LooksEmulated(device)))
            {
                notes.Add("A hypervisor is present. Emulated storage (AHCI/IDE) is slower than VirtIO, storvsc, or pvscsi.");
            }

            if (timer is { CurrentHundredNs: > 156250 })
            {
                notes.Add("The system timer resolution is coarser than 15.6 ms, which can add scheduling jitter.");
            }

            return OperationResult<VmEnvironment>.Ok(new VmEnvironment(
                hypervisor,
                vendor,
                WmiQuery.String(computer, "Manufacturer"),
                WmiQuery.String(computer, "Model"),
                WmiQuery.String(biosRow, "Manufacturer"),
                ReadTopology(),
                devices,
                timer,
                hvci,
                vbs,
                notes));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<VmEnvironment>.Fail("vm_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ProcessorTopology ReadTopology()
    {
        int logical = Environment.ProcessorCount;
        int cores = logical;
        int packages = 1;
        int numa = 1;
        try
        {
            uint size = 0;
            Span<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX> empty = default;
            _ = PInvoke.GetLogicalProcessorInformationEx(
                LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll,
                empty,
                ref size);
            byte[] buffer = new byte[Math.Max(size, 64)];
            Span<byte> bytes = buffer;
            if (!PInvoke.GetLogicalProcessorInformationEx(
                    LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll,
                    MemoryMarshal.Cast<byte, SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(bytes),
                    ref size))
            {
                return new ProcessorTopology(logical, cores, packages, numa);
            }

            cores = 0;
            packages = 0;
            numa = 0;
            int offset = 0;
            while (offset + 8 < size)
            {
                int relation = BitConverter.ToInt32(buffer, offset);
                int length = BitConverter.ToInt32(buffer, offset + 4);
                if (length <= 0)
                {
                    break;
                }

                switch (relation)
                {
                    case 0:
                        cores++;
                        break;
                    case 1:
                        numa++;
                        break;
                    case 3:
                        packages++;
                        break;
                }

                offset += length;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ProcessorTopology(logical, cores, packages, numa);
        }

        return new ProcessorTopology(logical, Math.Max(cores, 1), Math.Max(packages, 1), Math.Max(numa, 1));
    }

    private static ParavirtDevice MatchDevice(
        string kind,
        IReadOnlyList<WmiObject> pnp,
        IReadOnlyList<WmiObject> services,
        IReadOnlyList<string> tokens)
    {
        foreach (WmiObject row in pnp)
        {
            string? name = WmiQuery.String(row, "Name");
            string? service = WmiQuery.String(row, "Service");
            if (ContainsToken(name, tokens) || ContainsToken(service, tokens))
            {
                return new ParavirtDevice(kind, name, service, true);
            }
        }

        foreach (WmiObject row in services)
        {
            string? name = WmiQuery.String(row, "Name");
            if (ContainsToken(name, tokens))
            {
                return new ParavirtDevice(kind, name, name, true);
            }
        }

        return new ParavirtDevice(kind, null, null, false);
    }

    private static bool ContainsToken(string? value, IReadOnlyList<string> tokens) =>
        !string.IsNullOrWhiteSpace(value) && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool LooksVirtual(string? manufacturer, string? model)
    {
        string haystack = $"{manufacturer} {model}";
        return haystack.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("VMware", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("KVM", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("QEMU", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("Xen", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase);
    }

    private static string? InferVendor(string? manufacturer, string? model, string? bios)
    {
        string haystack = $"{manufacturer} {model} {bios}";
        if (haystack.Contains("VMware", StringComparison.OrdinalIgnoreCase))
        {
            return "VMware";
        }

        if (haystack.Contains("KVM", StringComparison.OrdinalIgnoreCase) || haystack.Contains("QEMU", StringComparison.OrdinalIgnoreCase))
        {
            return "KVM";
        }

        if (haystack.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) || haystack.Contains("innotek", StringComparison.OrdinalIgnoreCase))
        {
            return "VirtualBox";
        }

        if (haystack.Contains("Xen", StringComparison.OrdinalIgnoreCase))
        {
            return "Xen";
        }

        if (haystack.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && haystack.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
        {
            return "Hyper-V";
        }

        return LooksVirtual(manufacturer, model) ? "unknown-hypervisor" : null;
    }

    private static bool LooksEmulated(ParavirtDevice device) =>
        device.Service is not null
        && (device.Service.Contains("storahci", StringComparison.OrdinalIgnoreCase)
            || device.Service.Contains("atapi", StringComparison.OrdinalIgnoreCase)
            || device.Service.Contains("intelide", StringComparison.OrdinalIgnoreCase));
}
