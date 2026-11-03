using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Vestigo.Desktop;
using Vestigo.Devices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace Vestigo.Diagnostics;

/// <summary>Enumerates DXGI adapters in the service process and matches them to Display-class device nodes.</summary>
internal sealed class WindowsGraphicsInventory(IDeviceControl devices) : IGraphicsInventory
{
    public OperationResult<GraphicsAdapterList> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<GraphicsAdapterList>.Fail("windows_required", "Graphics adapter enumeration requires Windows.");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            return OperationResult<GraphicsAdapterList>.Fail("unsupported", "DXGI adapter details require Windows 10 version 1803 or later.");
        }

        try
        {
            IReadOnlyList<DeviceEntry> nodes = [];
            try
            {
                nodes = devices.EnumerateDevices(presentOnly: false);
            }
            catch (DeviceControlException)
            {
                nodes = [];
            }

            return OperationResult<GraphicsAdapterList>.Ok(new GraphicsAdapterList(Read(nodes)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult<GraphicsAdapterList>.Fail("unavailable", "DXGI is not available.");
        }
    }

    [SupportedOSPlatform("windows10.0.17134.0")]
    private static unsafe List<GraphicsAdapter> Read(IReadOnlyList<DeviceEntry> nodes)
    {
        HRESULT created = PInvoke.CreateDXGIFactory2(0, out IDXGIFactory6* factory);
        if (created.Failed || factory is null)
        {
            throw new EntryPointNotFoundException("CreateDXGIFactory2 failed.");
        }

        List<GraphicsAdapter> adapters = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter4* adapter;
                try
                {
                    factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_UNSPECIFIED, out adapter);
                }
                catch (Exception ex) when (ex.HResult == CaptureHresults.NotFound)
                {
                    break;
                }

                if (adapter is null)
                {
                    break;
                }

                try
                {
                    adapters.Add(Describe(adapter, nodes));
                }
                finally
                {
                    adapter->Release();
                }
            }
        }
        finally
        {
            factory->Release();
        }

        return adapters;
    }

    [SupportedOSPlatform("windows10.0.17134.0")]
    private static unsafe GraphicsAdapter Describe(IDXGIAdapter4* adapter, IReadOnlyList<DeviceEntry> nodes)
    {
        DXGI_ADAPTER_DESC3 desc = adapter->GetDesc3();
        uint flags = (uint)desc.Flags;
        uint? vendor = GraphicsAdapterMatcher.Id(desc.VendorId);
        uint? device = GraphicsAdapterMatcher.Id(desc.DeviceId);
        uint? subsystem = GraphicsAdapterMatcher.Id(desc.SubSysId);
        uint? revision = GraphicsAdapterMatcher.Id(desc.Revision);
        (string? instanceId, string? confidence) = GraphicsAdapterMatcher.Match(vendor, device, subsystem, revision, nodes);
        return new GraphicsAdapter(
            NullIfEmpty(desc.Description.ToString()),
            vendor,
            device,
            subsystem,
            revision,
            GraphicsAdapterMatcher.Luid(desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart),
            GraphicsAdapterMatcher.Bytes(desc.DedicatedVideoMemory),
            GraphicsAdapterMatcher.Bytes(desc.DedicatedSystemMemory),
            GraphicsAdapterMatcher.Bytes(desc.SharedSystemMemory),
            (flags & 2) != 0,
            (flags & 1) != 0,
            DriverVersion(adapter),
            instanceId,
            confidence,
            Outputs(adapter, GraphicsAdapterMatcher.Luid(desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart)));
    }

    [SupportedOSPlatform("windows10.0.17134.0")]
    private static unsafe string? DriverVersion(IDXGIAdapter4* adapter)
    {
        try
        {
            adapter->CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long version);
            return GraphicsAdapterMatcher.DriverVersion(version);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows10.0.17134.0")]
    private static unsafe List<GraphicsOutput> Outputs(IDXGIAdapter4* adapter, string? luid)
    {
        List<GraphicsOutput> outputs = [];
        string[] adapters = luid is null ? [] : [luid];
        for (uint index = 0; ; index++)
        {
            IDXGIOutput* output;
            HRESULT result = adapter->EnumOutputs(index, &output);
            if (result.Failed || output is null)
            {
                break;
            }

            try
            {
                DXGI_OUTPUT_DESC desc = output->GetDesc();
                int width = desc.DesktopCoordinates.right - desc.DesktopCoordinates.left;
                int height = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;
                outputs.Add(new GraphicsOutput(
                    NullIfEmpty(desc.DeviceName.ToString()),
                    desc.AttachedToDesktop,
                    width > 0 ? width : null,
                    height > 0 ? height : null,
                    DisplaySignalReader.TrimPrefix(desc.Rotation.ToString(), "DXGI_MODE_ROTATION_"),
                    adapters));
            }
            finally
            {
                output->Release();
            }
        }

        return outputs;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
