using System.Text;

namespace Vestigo.Diagnostics;

/// <summary>
///     Reads the documented dump prefix. <c>PAGEDU64</c> and <c>PAGEDUMP</c> share the first 16 bytes.
///     Machine type and processor count sit at different offsets, proven by the x64, ARM64, and i386 fixtures.
///     Bugcheck fields are not read.
/// </summary>
internal static class KernelDumpHeaderReader
{
    public const int PrefixBytes = 56;

    public static KernelDumpHeader Read(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 8)
        {
            return new KernelDumpHeader(null, null, null, null, null, null, null, "truncated");
        }

        string signature = Encoding.ASCII.GetString(prefix[..4]);
        string valid = Encoding.ASCII.GetString(prefix[4..8]);
        if (signature == "MDMP")
        {
            return new KernelDumpHeader("MDMP", null, null, null, null, null, null, null);
        }

        if (signature != "PAGE" || valid is not ("DU64" or "DUMP"))
        {
            return new KernelDumpHeader(null, null, null, null, null, null, null, "unrecognized");
        }

        if (prefix.Length < PrefixBytes)
        {
            return new KernelDumpHeader(signature, valid, null, null, null, null, null, "truncated");
        }

        int major = BitConverter.ToInt32(prefix[8..12]);
        int minor = BitConverter.ToInt32(prefix[12..16]);
        int machineOffset = valid == "DU64" ? 48 : 32;
        int processorOffset = machineOffset + 4;
        int machine = BitConverter.ToInt32(prefix.Slice(machineOffset, 4));
        int processors = BitConverter.ToInt32(prefix.Slice(processorOffset, 4));
        return new KernelDumpHeader(signature, valid, major, minor, machine, MachineName(machine), processors, null);
    }

    public static string FileKind(KernelDumpHeader? header) => header switch
    {
        { Signature: "PAGE", ValidDump: "DU64" } => "kernel64",
        { Signature: "PAGE", ValidDump: "DUMP" } => "kernel32",
        { Signature: "MDMP" } => "minidump",
        _ => "unknown"
    };

    public static string? CrashDumpEnabledName(int value) => value switch
    {
        0 => "none",
        1 => "complete",
        2 => "kernel",
        3 => "small",
        7 => "automatic",
        _ => null
    };

    private static string? MachineName(int machine) => machine switch
    {
        0x014C => "i386",
        0x8664 => "amd64",
        0xAA64 => "arm64",
        _ => null
    };
}
