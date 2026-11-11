using System.Globalization;
using System.Text.RegularExpressions;

namespace Vestigo.Diagnostics;

/// <summary>Parses the verifier output shapes observed on this Windows build. Unknown lines are kept.</summary>
internal static partial class VerifierStateParser
{
    internal const int MaxTextChars = 16_000;

    internal static string Bound(string? text, out bool truncated)
    {
        string value = text ?? string.Empty;
        if (value.Length <= MaxTextChars)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return value[..MaxTextChars];
    }

    internal static DriverVerifierQuery ParseRunning(string? text)
    {
        string bounded = Bound(text, out _);
        string trimmed = bounded.Trim();
        bool idle = trimmed.Equals("No drivers are currently verified.", StringComparison.Ordinal);
        return new DriverVerifierQuery(idle ? false : null, idle, bounded);
    }

    internal static DriverVerifierSettings ParseSettings(string? text)
    {
        string bounded = Bound(text, out _);
        uint? flags = null;
        List<VerifierFlag> standard = [];
        List<VerifierFlag> additional = [];
        List<string> ruleClasses = [];
        List<string> rules = [];
        List<string> drivers = [];
        List<string> unparsed = [];
        string? bootMode = null;
        string section = string.Empty;
        bool sawFlags = false;
        foreach (string raw in bounded.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("Verifier Flags:", StringComparison.Ordinal))
            {
                sawFlags = true;
                flags = TryHex(line["Verifier Flags:".Length..].Trim());
                if (flags is null)
                {
                    unparsed.Add(line);
                }

                section = string.Empty;
                continue;
            }

            string? next = Section(line);
            if (next is not null)
            {
                section = next;
                continue;
            }

            if (line.EndsWith(':'))
            {
                unparsed.Add(line);
                section = "unknown";
                continue;
            }

            if (section is "standard" or "additional" && FlagPattern().Match(line) is { Success: true } flag)
            {
                uint? value = TryHex(flag.Groups[2].Value);
                if (value is null)
                {
                    unparsed.Add(line);
                    continue;
                }

                VerifierFlag parsed = new(value.Value, flag.Groups[3].Value.Trim(), flag.Groups[1].Value == "X");
                (section == "standard" ? standard : additional).Add(parsed);
                continue;
            }

            switch (section)
            {
                case "classes":
                    ruleClasses.Add(line);
                    break;
                case "boot" when bootMode is null:
                    bootMode = line;
                    break;
                case "rules":
                    rules.Add(line);
                    break;
                case "drivers":
                    drivers.Add(line);
                    break;
                default:
                    unparsed.Add(line);
                    break;
            }
        }

        return new DriverVerifierSettings(flags, standard, additional, ruleClasses, bootMode, rules, drivers, unparsed, sawFlags);
    }

    private static string? Section(string line) => line switch
    {
        "Standard Flags:" => "standard",
        "Additional Flags:" => "additional",
        "Active Rule Classes:" => "classes",
        "Boot Mode:" => "boot",
        "Rules:" => "rules",
        "Verified Drivers:" => "drivers",
        _ => null,
    };

    private static uint? TryHex(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : null;

    [GeneratedRegex(@"^\[([ X])\]\s+(0x[0-9A-Fa-f]+)\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FlagPattern();
}
