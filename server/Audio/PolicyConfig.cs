using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;

namespace Vestigo.Audio;

/// <summary>
///     Undocumented Core Audio policy interface used to change the default endpoint. The IID and vtable are
///     stable on current Windows 10 and 11 releases, but Microsoft does not document this contract and it could
///     change.
/// </summary>
[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    void GetMixFormat();

    void GetDeviceFormat();

    void ResetDeviceFormat();

    void SetDeviceFormat();

    void GetProcessingPeriod();

    void SetProcessingPeriod();

    void GetShareMode();

    void SetShareMode();

    void GetPropertyValue();

    void SetPropertyValue();

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);

    void SetEndpointVisibility();
}

/// <summary>COM class that implements <see cref="IPolicyConfig" />. CLSID is undocumented.</summary>
[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient;
