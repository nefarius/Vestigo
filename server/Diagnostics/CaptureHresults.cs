namespace Vestigo.Diagnostics;

/// <summary>Named DXGI and access failures a capture probe may report. Other codes stay numeric.</summary>
internal static class CaptureHresults
{
    public const int AccessLost = unchecked((int)0x887A0026);

    public const int NotCurrentlyAvailable = unchecked((int)0x887A0022);

    public const int Unsupported = unchecked((int)0x887A0004);

    public const int AccessDenied = unchecked((int)0x80070005);

    public const int WaitTimeout = unchecked((int)0x887A0027);

    public const int NotFound = unchecked((int)0x887A0002);

    public static string? Name(int hr) => hr switch
    {
        AccessLost => "DXGI_ERROR_ACCESS_LOST",
        NotCurrentlyAvailable => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        Unsupported => "DXGI_ERROR_UNSUPPORTED",
        AccessDenied => "E_ACCESSDENIED",
        WaitTimeout => "DXGI_ERROR_WAIT_TIMEOUT",
        _ => null
    };

    public static string Hex(int hr) => "0x" + unchecked((uint)hr).ToString("X8");
}
