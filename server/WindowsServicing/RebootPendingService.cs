using Microsoft.Win32;

namespace Vestigo.WindowsServicing;

public sealed class ServicingRegistry : IServicingRegistry
{
    public bool KeyExists(string path)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(path, writable: false);
        return key is not null;
    }

    public object? Read(string path, string name)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(path, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

public sealed class RebootPendingService(IServicingRegistry registry)
{
    internal const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    internal const string Cbs = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    internal static readonly string[] QueueNames = ["PendingFileRenameOperations", "PendingFileRenameOperations2"];

    public RebootSnapshot RegistrySnapshot()
    {
        List<RebootSignal> signals = [];
        foreach ((string name, string path) in new[]
        {
            ("WindowsUpdate", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
            ("CbsReboot", Cbs + @"\RebootPending"),
            ("CbsPackages", Cbs + @"\PackagesPending"),
            ("CbsSessions", Cbs + @"\SessionsPending"),
            ("Installer", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")
        })
        {
            // InProgress and SessionsPending are evidence of activity, not authoritative reboot requests.
            signals.Add(Check(name, () => registry.KeyExists(path),
                name is "Installer" or "CbsSessions" ? "Presence indicates servicing/installer activity; reboot requirement is not authoritative." : null,
                name is "Installer" or "CbsSessions"));
        }
        foreach (string name in QueueNames)
        {
            signals.Add(Check(name, () => ReadQueue(name) is { Length: > 0 }));
        }
        signals.Add(Check("ComputerRename", () =>
        {
            const string path = @"SYSTEM\CurrentControlSet\Control\ComputerName";
            string active = registry.Read(path + @"\ActiveComputerName", "ComputerName") as string
                ?? throw new InvalidDataException();
            string configured = registry.Read(path + @"\ComputerName", "ComputerName") as string
                ?? throw new InvalidDataException();
            return !string.Equals(active, configured, StringComparison.OrdinalIgnoreCase);
        }));
        signals.Add(Check("DomainJoin", () => registry.Read(@"SYSTEM\CurrentControlSet\Services\Netlogon", "JoinDomain") is not null ||
            registry.Read(@"SYSTEM\CurrentControlSet\Services\Netlogon", "AvoidSpnSet") is not null));
        signals.Add(Check("UpdateExeVolatile", () => registry.Read(@"SOFTWARE\Microsoft\Updates", "UpdateExeVolatile") switch
        {
            null => false,
            int value => value != 0,
            _ => throw new InvalidDataException()
        }));
        return Snapshot(signals);
    }

    internal static RebootSnapshot Snapshot(IReadOnlyList<RebootSignal> signals) => new(
        signals.Any(s => s.State == "pending") ? "pending" : signals.Any(s => s.State == "unknown") ? "unknown" : "notPending", signals);

    private static RebootSignal Check(string source, Func<bool> read, string? detail = null, bool advisory = false)
    {
        try
        {
            bool present = read();
            return new(source, present ? advisory ? "unknown" : "pending" : "notPending", detail);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidDataException)
        {
            return new(source, "unknown", $"Read failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
        }
    }

    private string[]? ReadQueue(string name) => registry.Read(SessionManager, name) switch
    {
        null => null,
        string[] values => values,
        _ => throw new InvalidDataException("Expected REG_MULTI_SZ.")
    };

    public PendingFileQueue PendingFiles(int limit)
    {
        List<PendingFileOperation> operations = [];
        List<RebootSignal> sources = [];
        bool truncated = false;
        foreach (string name in QueueNames)
        {
            sources.Add(Check(name, () =>
            {
                string[] values = ReadQueue(name) ?? [];
                IReadOnlyList<PendingFileOperation> parsed = Parse(name, values, Math.Max(0, limit - operations.Count));
                operations.AddRange(parsed);
                truncated |= (values.Length + 1) / 2 > parsed.Count;
                return values.Length > 0;
            }));
        }
        return new(operations, truncated, sources);
    }

    internal static IReadOnlyList<PendingFileOperation> Parse(string name, string[] values, int limit)
    {
        List<PendingFileOperation> results = [];
        for (int i = 0; i < values.Length && results.Count < limit; i += 2)
        {
            string source = values[i];
            string? destination = i + 1 < values.Length ? values[i + 1] : null;
            bool replace = destination?.StartsWith('!') == true;
            results.Add(new(name, i / 2, destination is null || source.Length == 0 ? "malformed" :
                destination.Length == 0 ? "delete" : replace ? "replace" : "rename", source, destination,
                Normalize(source), Normalize(replace ? destination![1..] : destination),
                destination is null ? "Unpaired source; deletion cannot be inferred." : source.Length == 0 ? "Empty source." : null));
        }
        return results;
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' ? path : null;
    }
}
