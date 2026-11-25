namespace Vestigo.Desktop;

/// <summary>The lock screen and the UAC secure desktop both use this name on <c>WinSta0</c>.</summary>
internal static class SecureDesktopName
{
    internal const string Winlogon = "Winlogon";

    internal static bool IsSecure(string? name) =>
        string.Equals(name, Winlogon, StringComparison.OrdinalIgnoreCase);
}
