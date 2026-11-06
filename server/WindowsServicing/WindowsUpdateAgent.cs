using System.Runtime.InteropServices;

namespace Vestigo.WindowsServicing;

/// <summary>WUA automation objects never leave the calling MTA. Every acquired RCW is released in reverse order.</summary>
public sealed class WindowsUpdateAgent : IWindowsUpdateAgent
{
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        public dynamic Own(object value) { _objects.Add(value); return value; }
        public dynamic Create(string progId) => Own(Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!);
        public void Dispose()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(_objects[i])) Marshal.ReleaseComObject(_objects[i]);
        }
    }

    public bool RebootRequired()
    {
        using ComScope com = new();
        return (bool)com.Create("Microsoft.Update.SystemInfo").RebootRequired;
    }

    public WuaStatus Status()
    {
        using ComScope com = new();
        dynamic info = com.Create("Microsoft.Update.SystemInfo");
        dynamic session = com.Create("Microsoft.Update.Session");
        dynamic installer = com.Own(session.CreateUpdateInstaller());
        dynamic manager = com.Create("Microsoft.Update.ServiceManager");
        dynamic services = com.Own(manager.Services);
        List<UpdateServiceInfo> result = [];
        for (int i = 0; i < Math.Min((int)services.Count, 100); i++)
        {
            dynamic service = com.Own(services.Item(i));
            result.Add(new((string)service.Name, (string)service.ServiceID, (bool)service.IsManaged,
                (bool)service.IsDefaultAUService));
        }
        return new((bool)info.RebootRequired, (bool)installer.IsBusy, result);
    }

    internal static string Criteria(string state, bool drivers, bool hidden)
    {
        string installed = state switch
        {
            "pending" => "IsInstalled=0",
            "installed" => "IsInstalled=1",
            _ => throw new ArgumentException("state must be pending or installed.")
        };
        return installed + (drivers ? "" : " AND Type='Software'") + (hidden ? "" : " AND IsHidden=0");
    }

    public UpdateInventory Inventory(string state, bool drivers, bool hidden, int limit)
    {
        using ComScope com = new();
        dynamic session = com.Create("Microsoft.Update.Session");
        session.ClientApplicationID = "Vestigo read-only diagnostics";
        dynamic searcher = com.Own(session.CreateUpdateSearcher());
        searcher.Online = false; // No API parameter can enable online scanning in this module.
        dynamic result = com.Own(searcher.Search(Criteria(state, drivers, hidden)));
        dynamic updates = com.Own(result.Updates);
        int total = updates.Count;
        List<UpdateInfo> items = [];
        for (int i = 0; i < Math.Min(total, limit); i++)
        {
            dynamic update = com.Own(updates.Item(i));
            dynamic identity = com.Own(update.Identity);
            dynamic kbs = com.Own(update.KBArticleIDs);
            List<string> references = [];
            for (int j = 0; j < Math.Min((int)kbs.Count, 100); j++) references.Add((string)kbs.Item(j));
            items.Add(new((string)identity.UpdateID, (int)identity.RevisionNumber, (string)update.Title, references,
                (int)update.Type == 2 ? "driver" : "software", (bool)update.IsInstalled, (bool)update.IsDownloaded,
                (bool)update.IsHidden, (bool)update.RebootRequired));
        }
        dynamic warnings = com.Own(result.Warnings);
        List<string> warningCodes = [];
        for (int i = 0; i < Math.Min((int)warnings.Count, 100); i++)
        {
            dynamic warning = com.Own(warnings.Item(i));
            int code = warning.HResult;
            warningCodes.Add($"0x{code:X8}");
        }
        return new((int)result.ResultCode, new(items, total, total > items.Count), warningCodes);
    }

    public BoundedList<UpdateHistoryEntry> History(int offset, int limit, bool failuresOnly)
    {
        using ComScope com = new();
        dynamic session = com.Create("Microsoft.Update.Session");
        dynamic searcher = com.Own(session.CreateUpdateSearcher());
        int total = searcher.GetTotalHistoryCount();
        int count = Math.Min(limit, Math.Max(0, total - offset));
        if (count == 0) return new([], 0, false);
        dynamic history = com.Own(searcher.QueryHistory(offset, count));
        List<UpdateHistoryEntry> items = [];
        for (int i = 0; i < count; i++)
        {
            dynamic entry = com.Own(history.Item(i));
            int code = entry.ResultCode;
            if (failuresOnly && code is not (3 or 4 or 5)) continue;
            dynamic identity = com.Own(entry.UpdateIdentity);
            int hresult = entry.HResult;
            items.Add(new(DateTime.SpecifyKind((DateTime)entry.Date, DateTimeKind.Utc), (string)identity.UpdateID, (int)identity.RevisionNumber,
                (string)entry.Title, (int)entry.Operation, code, $"0x{hresult:X8}"));
        }
        return new(items, count, (long)offset + count < total);
    }
}
