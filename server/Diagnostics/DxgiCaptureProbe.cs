using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Diagnostics;

/// <summary>
///     Observes whether Desktop Duplication delivers frames. Pixel buffers are released without being read.
///     Windows Graphics Capture is not implemented: this build does not raise the target framework for WinRT.
/// </summary>
internal static class DxgiCaptureProbe
{
    private const uint DesktopRead = 0x0001;

    private const uint SdkVersion = 7;

    public static CaptureProbeResult Run(string target, TimeSpan duration, CancellationToken cancellationToken)
    {
        int session = Process.GetCurrentProcess().SessionId;
        string? desktop = DesktopName();
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 1))
        {
            return new CaptureProbeResult(session, desktop, "dxgi", target, new CaptureAdvertised(false, false), null);
        }

        try
        {
            return Probe(target, duration, session, desktop, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new CaptureProbeResult(session, desktop, "dxgi", target, new CaptureAdvertised(false, false), null);
        }
    }

    [SupportedOSPlatform("windows8.1")]
    private static unsafe CaptureProbeResult Probe(
        string target,
        TimeSpan duration,
        int session,
        string? desktop,
        CancellationToken cancellationToken)
    {
        HRESULT created = PInvoke.CreateDXGIFactory2(0, out IDXGIFactory6* factory);
        if (created.Failed || factory is null)
        {
            return new CaptureProbeResult(session, desktop, "dxgi", target, new CaptureAdvertised(false, false), null);
        }

        try
        {
            if (!TryOpenOutput(factory, target, out IDXGIAdapter4* adapter, out IDXGIOutput1* output, out int openError))
            {
                return new CaptureProbeResult(
                    session,
                    desktop,
                    "dxgi",
                    target,
                    new CaptureAdvertised(true, false),
                    openError == 0 ? null : new CaptureMeasured(0, null, CaptureHresults.Hex(openError), CaptureHresults.Name(openError)));
            }

            DuplicationSource? source = DuplicationSource.TryCreate(factory, adapter, output, out int startError);
            if (source is null)
            {
                adapter->Release();
                output->Release();
                return new CaptureProbeResult(
                    session,
                    desktop,
                    "dxgi",
                    target,
                    new CaptureAdvertised(true, true),
                    new CaptureMeasured(0, null, startError == 0 ? null : CaptureHresults.Hex(startError), CaptureHresults.Name(startError)));
            }

            CaptureMeasured measured = CaptureProbeLoop.Run(source, duration, cancellationToken);
            return new CaptureProbeResult(session, desktop, "dxgi", target, new CaptureAdvertised(true, true), measured);
        }
        finally
        {
            factory->Release();
        }
    }

    [SupportedOSPlatform("windows8.1")]
    private static unsafe bool TryOpenOutput(
        IDXGIFactory6* factory,
        string target,
        out IDXGIAdapter4* adapter,
        out IDXGIOutput1* output,
        out int error)
    {
        adapter = null;
        output = null;
        error = 0;
        bool primary = target.Equals("primary", StringComparison.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter4* candidate;
            try
            {
                factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_UNSPECIFIED, out candidate);
            }
            catch (Exception ex) when (ex.HResult == CaptureHresults.NotFound)
            {
                return false;
            }

            if (candidate is null)
            {
                return false;
            }

            if (TryMatchOutput(candidate, target, primary, out output))
            {
                adapter = candidate;
                return true;
            }

            candidate->Release();
        }
    }

    [SupportedOSPlatform("windows8.1")]
    private static unsafe bool TryMatchOutput(IDXGIAdapter4* adapter, string target, bool primary, out IDXGIOutput1* matched)
    {
        matched = null;
        for (uint index = 0; ; index++)
        {
            IDXGIOutput* output;
            if (adapter->EnumOutputs(index, &output).Failed || output is null)
            {
                return false;
            }

            bool take = false;
            try
            {
                DXGI_OUTPUT_DESC desc = output->GetDesc();
                string name = desc.DeviceName.ToString();
                bool containsOrigin = desc.DesktopCoordinates.left <= 0 && desc.DesktopCoordinates.right > 0 &&
                                      desc.DesktopCoordinates.top <= 0 && desc.DesktopCoordinates.bottom > 0;
                take = primary
                    ? desc.AttachedToDesktop && containsOrigin
                    : name.Equals(target, StringComparison.OrdinalIgnoreCase);
                if (take && output->QueryInterface(out matched).Succeeded && matched is not null)
                {
                    return true;
                }

                take = false;
                matched = null;
            }
            finally
            {
                output->Release();
            }
        }
    }

    private static unsafe string? DesktopName()
    {
        HDESK input = PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)DesktopRead);
        if (input.IsNull)
        {
            return null;
        }

        try
        {
            uint needed = 0;
            PInvoke.GetUserObjectInformation((HANDLE)input.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, null, 0, &needed);
            if (needed < 2)
            {
                return null;
            }

            byte[] buffer = new byte[needed];
            fixed (byte* pointer = buffer)
            {
                if (!PInvoke.GetUserObjectInformation((HANDLE)input.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, pointer, needed, &needed))
                {
                    return null;
                }
            }

            return MemoryMarshal.Cast<byte, char>(buffer.AsSpan()).ToString().TrimEnd('\0');
        }
        finally
        {
            PInvoke.CloseDesktop(input);
        }
    }

    [SupportedOSPlatform("windows8.1")]
    private sealed unsafe class DuplicationSource : ICaptureFrameSource
    {
        private IDXGIOutputDuplication* _duplication;
        private ID3D11Device* _device;
        private ID3D11DeviceContext* _context;
        private IDXGIOutput1* _output;
        private IDXGIAdapter4* _adapter;
        private bool _disposed;

        private DuplicationSource(
            IDXGIOutputDuplication* duplication,
            ID3D11Device* device,
            ID3D11DeviceContext* context,
            IDXGIOutput1* output,
            IDXGIAdapter4* adapter)
        {
            _duplication = duplication;
            _device = device;
            _context = context;
            _output = output;
            _adapter = adapter;
        }

        public static DuplicationSource? TryCreate(IDXGIFactory6* factory, IDXGIAdapter4* adapter, IDXGIOutput1* output, out int error)
        {
            _ = factory;
            error = 0;
            ID3D11Device* device = null;
            ID3D11DeviceContext* context = null;
            IDXGIOutputDuplication* duplication = null;
            try
            {
                D3D_FEATURE_LEVEL[] levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
                HRESULT created = PInvoke.D3D11CreateDevice(
                    (IDXGIAdapter*)adapter,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                    default,
                    0,
                    levels,
                    SdkVersion,
                    &device,
                    out _,
                    &context);
                if (created.Failed || device is null)
                {
                    error = created.Value;
                    return null;
                }

                output->DuplicateOutput((IUnknown*)device, &duplication);
                if (duplication is null)
                {
                    error = CaptureHresults.Unsupported;
                    return null;
                }

                return new DuplicationSource(duplication, device, context, output, adapter);
            }
            catch (Exception ex)
            {
                error = ex.HResult;
                if (duplication is not null)
                {
                    duplication->Release();
                }

                if (context is not null)
                {
                    context->Release();
                }

                if (device is not null)
                {
                    device->Release();
                }

                return null;
            }
        }

        public bool TryAcquire(int timeoutMs, out int hresult)
        {
            hresult = 0;
            IDXGIResource* resource = null;
            try
            {
                DXGI_OUTDUPL_FRAME_INFO info;
                _duplication->AcquireNextFrame((uint)Math.Max(timeoutMs, 0), &info, &resource);
                return true;
            }
            catch (Exception ex)
            {
                hresult = ex.HResult;
                return false;
            }
            finally
            {
                if (resource is not null)
                {
                    resource->Release();
                }

                if (hresult == 0)
                {
                    try
                    {
                        _duplication->ReleaseFrame();
                    }
                    catch (Exception ex)
                    {
                        hresult = ex.HResult;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_duplication is not null)
            {
                _duplication->Release();
                _duplication = null;
            }

            if (_context is not null)
            {
                _context->Release();
                _context = null;
            }

            if (_device is not null)
            {
                _device->Release();
                _device = null;
            }

            if (_output is not null)
            {
                _output->Release();
                _output = null;
            }

            if (_adapter is not null)
            {
                _adapter->Release();
                _adapter = null;
            }
        }
    }
}
