using Vestigo.Abstractions;

namespace Vestigo.Diagnostics;

/// <summary>Builds the only verifier argument lists Vestigo will run. There is no <c>/all</c> form.</summary>
internal static class VerifierControlPlanner
{
    internal static readonly string[] ProtectedImages =
    [
        "ntoskrnl.exe",
        "hal.dll",
        "win32k.sys",
        "win32kbase.sys",
        "win32kfull.sys",
        "cng.sys",
        "ksecdd.sys",
        "ci.dll",
    ];

    internal static readonly (string Name, uint Bit)[] StandardFlags =
    [
        ("specialPool", 0x1),
        ("forceIrqlChecking", 0x2),
        ("poolTracking", 0x8),
        ("ioVerification", 0x10),
        ("deadlockDetection", 0x20),
        ("dmaChecking", 0x80),
        ("securityChecks", 0x100),
        ("miscellaneousChecks", 0x800),
        ("ddiCompliance", 0x20000),
    ];

    internal static OperationResult<IReadOnlyList<string>> Enable(IReadOnlyList<string>? drivers, IReadOnlyList<string>? flags, bool acknowledged)
    {
        if (!acknowledged)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                "verifier_acknowledgement_required",
                "Set iUnderstandVerifierCanBugcheck to true. Verifier can bugcheck after the next boot, and Vestigo does not reboot or clear it for you.");
        }

        if (drivers is not { Count: >= 1 and <= 8 })
        {
            return OperationResult<IReadOnlyList<string>>.Fail("invalid_request", "Name 1 to 8 drivers. Verifying every driver is not available.");
        }

        List<string> names = [];
        foreach (string driver in drivers)
        {
            if (!IsDriverName(driver))
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    "invalid_request",
                    "Each driver must be one image name ending in .sys, with no path or wildcard.");
            }

            if (ProtectedImages.Contains(driver, StringComparer.OrdinalIgnoreCase))
            {
                return OperationResult<IReadOnlyList<string>>.Fail("protected_driver", driver + " is a protected system image and is refused.");
            }

            names.Add(driver);
        }

        List<string> arguments = [];
        if (flags is null || flags.Count == 0)
        {
            arguments.Add("/standard");
        }
        else
        {
            uint mask = 0;
            foreach (string flag in flags)
            {
                (string Name, uint Bit) known = StandardFlags.FirstOrDefault(item => item.Name.Equals(flag, StringComparison.OrdinalIgnoreCase));
                if (known.Name is null)
                {
                    return OperationResult<IReadOnlyList<string>>.Fail(
                        "invalid_request",
                        "Flags must be standard verifier names. Additional flags, rule classes, and raw bit values are not accepted.");
                }

                mask |= known.Bit;
            }

            arguments.Add("/flags");
            arguments.Add("0x" + mask.ToString("X8"));
        }

        arguments.Add("/driver");
        arguments.AddRange(names);
        return OperationResult<IReadOnlyList<string>>.Ok(arguments);
    }

    internal static IReadOnlyList<string> Reset() => ["/reset"];

    internal static bool IsAllowed(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1 && arguments[0] == "/reset")
        {
            return true;
        }

        if (arguments.Count >= 3 && arguments[0] == "/standard" && arguments[1] == "/driver")
        {
            return arguments.Skip(2).All(IsDriverName) && arguments.Skip(2).All(name => !ProtectedImages.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        if (arguments.Count >= 4 && arguments[0] == "/flags" && arguments[2] == "/driver" && IsStandardMask(arguments[1]))
        {
            return arguments.Skip(3).All(IsDriverName) && arguments.Skip(3).All(name => !ProtectedImages.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsStandardMask(string text)
    {
        if (!text.StartsWith("0x", StringComparison.Ordinal) || !uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint mask) || mask == 0)
        {
            return false;
        }

        uint standard = 0;
        foreach ((string _, uint bit) in StandardFlags)
        {
            standard |= bit;
        }

        return (mask & ~standard) == 0;
    }

    private static bool IsDriverName(string? name) =>
        name is { Length: >= 5 and <= 64 }
        && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
        && !name.Contains("..", StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(name[0]);
}
