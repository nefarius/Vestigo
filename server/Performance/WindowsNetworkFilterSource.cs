using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

using WmiLight;

namespace Vestigo.Performance;

/// <summary>NDIS Lightweight Filter bindings plus WFP callouts, sublayers, and providers.</summary>
public sealed class WindowsNetworkFilterSource : INetworkFilterSource
{
    private const uint RpcAuthnWinnt = 10;
    private const uint AccessDenied = 5;
    private const uint FwpEAccessDenied = 0x80320006;
    private const uint EnumBatch = 256;

    public OperationResult<NetworkFilterList> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkFilterList>.Fail("windows_required", "Network filter inventory requires Windows.");
        }

        try
        {
            List<NdisBinding> bindings = NdisBindings();
            (IReadOnlyList<WfpObject> wfp, bool partial, string? error) = EnumerateWfp();
            return OperationResult<NetworkFilterList>.Ok(new NetworkFilterList(bindings, wfp, partial, error));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<NetworkFilterList>.Fail("network_filter_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<NdisBinding> NdisBindings()
    {
        List<NdisBinding> list = [];
        foreach (WmiObject row in WmiQuery.Query(
                     @"\\.\ROOT\StandardCimv2",
                     "SELECT Name, ComponentID, DisplayName, Enabled FROM MSFT_NetAdapterBindingSettingData"))
        {
            string? component = WmiQuery.String(row, "ComponentID");
            string? display = WmiQuery.String(row, "DisplayName");
            if (component is null && display is null)
            {
                continue;
            }

            list.Add(new NdisBinding(
                WmiQuery.String(row, "Name"),
                component,
                display,
                WmiQuery.Boolean(row, "Enabled"),
                Kind(component, display)));
        }

        return list;
    }

    internal static string Kind(string? component, string? display)
    {
        string text = $"{component} {display}";
        if (text.Contains("wfplwfs", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("wfp", StringComparison.OrdinalIgnoreCase))
        {
            return "wfp";
        }

        if (text.Contains("ndis", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            return "lwf";
        }

        return "binding";
    }

    private static (IReadOnlyList<WfpObject> Objects, bool Partial, string? Error) EnumerateWfp()
    {
        uint opened = PInvoke.FwpmEngineOpen0(RpcAuthnWinnt, null, null, out FwpmEngineClose0SafeHandle engine);
        if (opened != 0)
        {
            engine.Dispose();
            return ([], IsAccessDenied(opened), opened is AccessDenied or FwpEAccessDenied
                ? "WFP engine open was denied."
                : $"WFP engine open failed ({opened}).");
        }

        try
        {
            List<WfpObject> objects = [];
            bool partial = false;
            string? error = null;
            Collect(engine, "callout", objects, ref partial, ref error, EnumerateCallouts);
            Collect(engine, "sublayer", objects, ref partial, ref error, EnumerateSubLayers);
            Collect(engine, "provider", objects, ref partial, ref error, EnumerateProviders);
            return (objects, partial, error);
        }
        finally
        {
            engine.Dispose();
        }
    }

    private delegate uint Enumerator(SafeHandle engine, List<WfpObject> objects);

    private static void Collect(SafeHandle engine, string kind, List<WfpObject> objects, ref bool partial, ref string? error, Enumerator enumerate)
    {
        uint status = enumerate(engine, objects);
        if (status == 0)
        {
            return;
        }

        partial = true;
        string message = IsAccessDenied(status)
            ? $"WFP {kind} enumeration was denied."
            : $"WFP {kind} enumeration failed ({status}).";
        error = error is null ? message : error + " " + message;
    }

    private static unsafe uint EnumerateCallouts(SafeHandle engine, List<WfpObject> objects)
    {
        uint created = PInvoke.FwpmCalloutCreateEnumHandle0(engine, null, out FWPM_CALLOUT_ENUM_HANDLE handle);
        if (created != 0)
        {
            return created;
        }

        try
        {
            while (true)
            {
                uint status = PInvoke.FwpmCalloutEnum0(engine, new FakeHandle((nint)handle.Value), EnumBatch, out FWPM_CALLOUT0** entries, out uint returned);
                if (status != 0 || returned == 0 || entries is null)
                {
                    return status;
                }

                try
                {
                    for (uint index = 0; index < returned; index++)
                    {
                        FWPM_CALLOUT0* item = entries[index];
                        if (item is null)
                        {
                            continue;
                        }

                        objects.Add(new WfpObject(
                            "callout",
                            Display(item->displayData.name),
                            Display(item->displayData.description),
                            item->calloutKey.ToString(),
                            item->providerKey is null ? null : item->providerKey->ToString()));
                    }
                }
                finally
                {
                    void* memory = entries;
                    PInvoke.FwpmFreeMemory0(ref memory);
                }

                if (returned < EnumBatch)
                {
                    return 0;
                }
            }
        }
        finally
        {
            PInvoke.FwpmCalloutDestroyEnumHandle0(new FWPM_ENGINE_HANDLE(engine.DangerousGetHandle()), handle);
        }
    }

    private static unsafe uint EnumerateSubLayers(SafeHandle engine, List<WfpObject> objects)
    {
        uint created = PInvoke.FwpmSubLayerCreateEnumHandle0(engine, null, out FWPM_SUBLAYER_ENUM_HANDLE handle);
        if (created != 0)
        {
            return created;
        }

        try
        {
            while (true)
            {
                uint status = PInvoke.FwpmSubLayerEnum0(engine, new FakeHandle((nint)handle.Value), EnumBatch, out FWPM_SUBLAYER0** entries, out uint returned);
                if (status != 0 || returned == 0 || entries is null)
                {
                    return status;
                }

                try
                {
                    for (uint index = 0; index < returned; index++)
                    {
                        FWPM_SUBLAYER0* item = entries[index];
                        if (item is null)
                        {
                            continue;
                        }

                        objects.Add(new WfpObject(
                            "sublayer",
                            Display(item->displayData.name),
                            Display(item->displayData.description),
                            item->subLayerKey.ToString(),
                            item->providerKey is null ? null : item->providerKey->ToString()));
                    }
                }
                finally
                {
                    void* memory = entries;
                    PInvoke.FwpmFreeMemory0(ref memory);
                }

                if (returned < EnumBatch)
                {
                    return 0;
                }
            }
        }
        finally
        {
            PInvoke.FwpmSubLayerDestroyEnumHandle0(new FWPM_ENGINE_HANDLE(engine.DangerousGetHandle()), handle);
        }
    }

    private static unsafe uint EnumerateProviders(SafeHandle engine, List<WfpObject> objects)
    {
        uint created = PInvoke.FwpmProviderCreateEnumHandle0(engine, null, out FWPM_PROVIDER_ENUM_HANDLE handle);
        if (created != 0)
        {
            return created;
        }

        try
        {
            while (true)
            {
                uint status = PInvoke.FwpmProviderEnum0(engine, new FakeHandle((nint)handle.Value), EnumBatch, out FWPM_PROVIDER0** entries, out uint returned);
                if (status != 0 || returned == 0 || entries is null)
                {
                    return status;
                }

                try
                {
                    for (uint index = 0; index < returned; index++)
                    {
                        FWPM_PROVIDER0* item = entries[index];
                        if (item is null)
                        {
                            continue;
                        }

                        objects.Add(new WfpObject(
                            "provider",
                            Display(item->displayData.name),
                            Display(item->displayData.description),
                            item->providerKey.ToString(),
                            null));
                    }
                }
                finally
                {
                    void* memory = entries;
                    PInvoke.FwpmFreeMemory0(ref memory);
                }

                if (returned < EnumBatch)
                {
                    return 0;
                }
            }
        }
        finally
        {
            PInvoke.FwpmProviderDestroyEnumHandle0(new FWPM_ENGINE_HANDLE(engine.DangerousGetHandle()), handle);
        }
    }

    private static unsafe string? Display(PWSTR value) =>
        value.Value == null ? null : value.ToString();

    private static bool IsAccessDenied(uint status) =>
        status is AccessDenied or FwpEAccessDenied or (uint)WIN32_ERROR.ERROR_ACCESS_DENIED;

    private sealed class FakeHandle(nint value) : SafeHandle(value, ownsHandle: false)
    {
        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle() => true;
    }
}
