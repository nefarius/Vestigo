using System.Text;

using Vestigo.Abstractions;

namespace Vestigo.Software;

/// <summary>A command line split into an executable and argument tokens.</summary>
public sealed record SplitCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
///     Turns an inventory entry into a silent uninstall command. Uninstall strings are never handed to a shell;
///     the executable must be an existing absolute path (or <c>msiexec.exe</c> from the system directory).
/// </summary>
public sealed class UninstallPlanner
{
    private static readonly string[] InnoSwitches = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"];
    private static readonly string[] BurnSwitches = ["/quiet", "/norestart"];

    public OperationResult<UninstallPlan> Plan(InstalledSoftware software, string? msiLogPath)
    {
        if (software.IsMsi)
        {
            if (software.ProductCode is not { } code)
            {
                return Fail("unsupported_installer", "MSI entry has no product code.");
            }

            string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            List<string> args = ["/x", code, "/qn", "/norestart"];
            if (msiLogPath is not null)
            {
                args.Add("/l*v");
                args.Add(msiLogPath);
            }

            return OperationResult<UninstallPlan>.Ok(new UninstallPlan(msiexec, args, InstallerTypes.Msi, "msi", msiLogPath));
        }

        if (!string.IsNullOrWhiteSpace(software.QuietUninstallString))
        {
            string[] restart = software.InstallerType switch
            {
                InstallerTypes.Burn => ["/norestart"],
                InstallerTypes.Inno => ["/NORESTART"],
                _ => []
            };
            return FromString(software.QuietUninstallString, software.InstallerType, "QuietUninstallString", restart);
        }

        if (string.IsNullOrWhiteSpace(software.UninstallString))
        {
            return Fail("no_uninstall_string", "The entry has no uninstall command.");
        }

        return software.InstallerType switch
        {
            InstallerTypes.Burn => FromString(software.UninstallString, software.InstallerType, "burn", BurnSwitches, "/uninstall"),
            InstallerTypes.Inno => FromString(software.UninstallString, software.InstallerType, "inno", InnoSwitches),
            InstallerTypes.Nsis => FromString(software.UninstallString, software.InstallerType, "nsis", ["/S"]),
            _ => Fail(
                "unsupported_installer",
                $"No known silent switches for installer type '{software.InstallerType}' and the entry has no QuietUninstallString.")
        };
    }

    private static OperationResult<UninstallPlan> FromString(
        string commandLine,
        string type,
        string source,
        IReadOnlyList<string> switches,
        string? required = null)
    {
        SplitCommand? split = SplitCommandLine(commandLine, type);
        if (split is null)
        {
            return Fail("invalid_uninstall_string", "The uninstall command could not be parsed.");
        }

        if (!InstallerDetector.IsLocalDrivePath(split.Executable) || !File.Exists(split.Executable))
        {
            return Fail("invalid_uninstall_string", $"Uninstaller '{split.Executable}' is not an existing path on a local drive.");
        }

        List<string> args = [.. split.Arguments];
        if (required is not null)
        {
            if (!args.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                args.Add(required);
            }
        }

        foreach (string option in switches)
        {
            if (!args.Contains(option, StringComparer.OrdinalIgnoreCase))
            {
                args.Add(option);
            }
        }

        return OperationResult<UninstallPlan>.Ok(new UninstallPlan(split.Executable, args, type, source, null));
    }

    /// <summary>
    ///     Splits a registry command line. Handles quoted executables and unquoted paths that contain spaces
    ///     (resolved to the longest existing <c>.exe</c> prefix).
    /// </summary>
    public static SplitCommand? SplitCommandLine(string commandLine, string? installerType = null)
    {
        bool crt = installerType == InstallerTypes.Burn;
        string text = commandLine.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string executable;
        string rest;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            executable = text[1..end];
            rest = text[(end + 1)..];
        }
        else
        {
            int split = FindExecutableEnd(text);
            executable = text[..split];
            rest = text[split..];
        }

        return executable.Length == 0 ? null : new SplitCommand(executable, crt ? TokenizeCrt(rest) : Tokenize(rest));
    }

    /// <summary>
    ///     MSVCRT rules, used by WiX Burn: <c>2n</c> backslashes before a quote give <c>n</c> backslashes and toggle
    ///     quoting, <c>2n+1</c> give <c>n</c> backslashes and a literal quote; other backslashes are literal.
    /// </summary>
    private static List<string> TokenizeCrt(string text)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        bool inQuotes = false;
        bool started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                int count = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    count++;
                    i++;
                }

                started = true;
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static int FindExecutableEnd(string text)
    {
        int index = 0;
        int firstExe = -1;
        while ((index = text.IndexOf(".exe", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = index + 4;
            if (end == text.Length || text[end] == ' ')
            {
                if (firstExe < 0)
                {
                    firstExe = end;
                }

                if (InstallerDetector.IsLocalDrivePath(text) && File.Exists(text[..end]))
                {
                    return end;
                }
            }

            index = end;
        }

        if (firstExe >= 0)
        {
            return firstExe;
        }

        int space = text.IndexOf(' ');
        return space < 0 ? text.Length : space;
    }

    private static List<string> Tokenize(string text)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        bool inQuotes = false;
        bool started = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static OperationResult<UninstallPlan> Fail(string code, string message) =>
        OperationResult<UninstallPlan>.Fail(code, message);
}
