using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Vestigo.Processes;

namespace Vestigo.RemoteDebugger;

/// <summary>Visual Studio generation whose Remote Debugging Tools are managed.</summary>
public enum DebuggerVersion
{
    /// <summary>Visual Studio 2022 (17.x).</summary>
    Vs2022 = 17,

    /// <summary>Visual Studio 2026 (18.x).</summary>
    Vs2026 = 18
}

/// <summary>Architecture of <c>msvsmon.exe</c> (the debuggee's architecture).</summary>
public enum DebuggerArch
{
    X64,
    Arm64,
    X86
}

/// <summary>Authentication mode of the remote debugging monitor.</summary>
public enum DebuggerAuth
{
    Windows,
    None
}

/// <summary>Pure parsing, URL selection, argument building, and validation for the remote debugger tools.</summary>
public static partial class RemoteDebuggerPlanner
{
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 86400;
    public const int DefaultTimeoutSeconds = 3600;
    public const string NoAuthWarning =
        "Authentication is disabled (/noauth /anyuser): anyone who can reach the port can attach to and run code in any process "
        + "the debugger user can debug. Use only on an isolated network and stop the monitor when done.";

    public static IReadOnlyList<string> InstallArguments { get; } = ["/install", "/quiet", "/norestart"];

    public static IReadOnlyList<string> PrepareArguments { get; } = ["/prepcomputer", "/private", "/quiet"];

    public static bool TryParseVersion(string? value, out DebuggerVersion version)
    {
        version = default;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "2022" or "17" or "vs2022":
                version = DebuggerVersion.Vs2022;
                return true;
            case "2026" or "18" or "vs2026":
                version = DebuggerVersion.Vs2026;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseArch(string? value, out DebuggerArch arch)
    {
        arch = DebuggerArch.X64;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "x64" or "amd64":
                return true;
            case "arm64":
                arch = DebuggerArch.Arm64;
                return true;
            case "x86":
                arch = DebuggerArch.X86;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseAuth(string? value, out DebuggerAuth auth)
    {
        auth = DebuggerAuth.Windows;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "windows":
                return true;
            case "none":
                auth = DebuggerAuth.None;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Directory name under <c>Remote Debugger</c> for an architecture.</summary>
    public static string ArchFolder(DebuggerArch arch) => arch switch
    {
        DebuggerArch.X64 => "x64",
        DebuggerArch.Arm64 => "arm64",
        _ => "x86"
    };

    public static string VersionLabel(DebuggerVersion version) => version == DebuggerVersion.Vs2022 ? "2022" : "2026";

    /// <summary>The aka.ms installer URL. The 2026 arm64 URL follows the same pattern but is unverified.</summary>
    public static Uri InstallerUrl(DebuggerVersion version, DebuggerArch arch)
    {
        string token = arch switch
        {
            DebuggerArch.X64 => "amd64",
            DebuggerArch.Arm64 => "arm64",
            _ => "x86"
        };
        return new Uri($"https://aka.ms/vs/{(int)version}/release/RemoteTools.{token}ret.enu.exe");
    }

    /// <summary>Returns an error message when the start request is not acceptable, otherwise null.</summary>
    public static string? ValidateStart(
        int port,
        int timeoutSeconds,
        DebuggerAuth auth,
        bool confirmNoAuth,
        IReadOnlyList<string> allowUsers)
    {
        if (port is < 1 or > 65535)
        {
            return "port must be between 1 and 65535.";
        }

        if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            return $"timeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.";
        }

        if (auth == DebuggerAuth.None)
        {
            if (!confirmNoAuth)
            {
                return "auth 'none' disables authentication completely. Pass iUnderstandNoAuthIsInsecure: true to confirm.";
            }

            if (allowUsers.Count > 0)
            {
                return "allowUsers cannot be combined with auth 'none'.";
            }
        }

        foreach (string user in allowUsers)
        {
            if (!UserPattern().IsMatch(user))
            {
                return $"allowUsers entry '{user}' is not a valid DOMAIN\\user or user@domain name.";
            }
        }

        return null;
    }

    /// <summary>Builds the msvsmon arguments. Call <see cref="ValidateStart" /> first.</summary>
    public static IReadOnlyList<string> BuildStartArguments(
        int port,
        int timeoutSeconds,
        DebuggerAuth auth,
        IReadOnlyList<string> allowUsers)
    {
        List<string> args = ["/nostatus", "/silent", "/nofirewallwarn", "/port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/timeout", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (auth == DebuggerAuth.None)
        {
            args.Add("/noauth");
            args.Add("/anyuser");
        }
        else
        {
            foreach (string user in allowUsers)
            {
                args.Add("/allow");
                args.Add(user);
            }
        }

        return args;
    }

    /// <summary>Space-joined arguments for display; arguments with spaces are quoted. Contains no secrets.</summary>
    public static string Describe(IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(arg => arg.Contains(' ') ? "\"" + arg + "\"" : arg));

    /// <summary>
    ///     True when an Authenticode signer subject names Microsoft Corporation in its CN or O component
    ///     (an exact component match, so "Evil Microsoft Corporation Ltd" is rejected).
    /// </summary>
    public static bool IsMicrosoftSigner(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        try
        {
            X500DistinguishedName name = new(subject);
            foreach (X500RelativeDistinguishedName part in name.EnumerateRelativeDistinguishedNames())
            {
                string? type = part.GetSingleElementType().FriendlyName;
                string? value = part.GetSingleElementValue();
                if ((string.Equals(type, "CN", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "O", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(part.GetSingleElementType().Value, "2.5.4.3", StringComparison.Ordinal)
                     || string.Equals(part.GetSingleElementType().Value, "2.5.4.10", StringComparison.Ordinal))
                    && string.Equals(value?.Trim(), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Extracts the <c>/port</c> value from a msvsmon command line, or null.</summary>
    public static int? ParsePort(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        Match match = PortPattern().Match(commandLine);
        return match.Success && int.TryParse(match.Groups[1].Value, out int port) && port is >= 1 and <= 65535 ? port : null;
    }

    /// <summary>The full command line for an argument list; used for length checks and tests.</summary>
    public static string CommandLine(string executable, IReadOnlyList<string> args) => CommandLineBuilder.Build(executable, args);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$ ]{1,128}(\\[A-Za-z0-9_.\-$ ]{1,128}|@[A-Za-z0-9_.\-]{1,255})?$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"(?:^|\s)/port(?:\s+|:)(\d{1,5})(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PortPattern();
}