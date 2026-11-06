namespace Vestigo.WindowsServicing;

public sealed record RebootSignal(string Source, string State, string? Detail = null);
public sealed record RebootSnapshot(string State, IReadOnlyList<RebootSignal> Signals);
public sealed record PendingFileOperation(string ValueName, int Index, string Operation,
    string Source, string? Destination, string? SourcePath, string? DestinationPath, string? Warning);
public sealed record PendingFileQueue(IReadOnlyList<PendingFileOperation> Operations, bool Truncated,
    IReadOnlyList<RebootSignal> Sources);
public sealed record BoundedList<T>(IReadOnlyList<T> Items, int Examined, bool Truncated);
public sealed record UpdateInfo(string Id, int Revision, string Title, IReadOnlyList<string> KBs,
    string Type, bool Installed, bool Downloaded, bool Hidden, bool RebootRequired);
public sealed record UpdateInventory(int ResultCode, BoundedList<UpdateInfo> Updates, IReadOnlyList<string> WarningHResults);
public sealed record UpdateHistoryEntry(DateTime Date, string Id, int Revision, string Title,
    int Operation, int ResultCode, string HResult);
public sealed record UpdateServiceInfo(string Name, string Id, bool Managed, bool Default);
public sealed record WuaStatus(bool RebootRequired, bool InstallerBusy, IReadOnlyList<UpdateServiceInfo> Services);
public sealed record ServicingPackage(string Name, string State, int StateCode, int ReleaseType);
public sealed record ServicingFile(string Name, string State, long? Length, DateTime? LastWriteUtc);

/// <summary>Registry seam: missing is null/false; access errors must throw, never become absence.</summary>
public interface IServicingRegistry
{
    bool KeyExists(string path);
    object? Read(string path, string name);
}

public interface IWindowsUpdateAgent
{
    bool RebootRequired();
    WuaStatus Status();
    UpdateInventory Inventory(string state, bool drivers, bool hidden, int limit);
    BoundedList<UpdateHistoryEntry> History(int offset, int limit, bool failuresOnly);
}

public sealed class WindowsServicingOptions
{
    public int MaxResults { get; set; } = 200;
    public int MaxLogBytes { get; set; } = 256 * 1024;
    public int MaxLogLines { get; set; } = 200;
    public TimeSpan NativeCallTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (MaxResults is < 1 or > 2000 || MaxLogBytes is < 16 or > 4 * 1024 * 1024 ||
            MaxLogLines is < 1 or > 2000 || NativeCallTimeout <= TimeSpan.Zero || NativeCallTimeout > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("Vestigo:WindowsServicing limits are out of range.");
        }
    }
}
