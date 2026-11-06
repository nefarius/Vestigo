using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.UpdateAgent;

namespace Vestigo.WindowsServicing;

/// <summary>CsWin32 WUA interfaces and owned BSTRs remain on the calling MTA.</summary>
public sealed unsafe class WindowsUpdateAgent : IWindowsUpdateAgent
{
    /// <summary>Owns each acquired COM reference once, even when multiple references share an address.</summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<nint> _interfaces = [];
        private readonly List<SysFreeStringSafeHandle> _strings = [];

        public ComScope() => PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED).ThrowOnFailure();

        public T* Own<T>(T* value) where T : unmanaged
        {
            if (value is null)
            {
                throw new InvalidOperationException("A successful COM call returned a null interface.");
            }

            _interfaces.Add((nint)value);
            return value;
        }

        public IUpdateSession* CreateSession()
        {
            UpdateSession.CreateInstance(out IUpdateSession* value).ThrowOnFailure();
            return Own(value);
        }

        public ISystemInformation* CreateSystemInfo()
        {
            SystemInformation.CreateInstance(out ISystemInformation* value).ThrowOnFailure();
            return Own(value);
        }

        public IUpdateServiceManager* CreateServiceManager()
        {
            UpdateServiceManager.CreateInstance(out IUpdateServiceManager* value).ThrowOnFailure();
            return Own(value);
        }

        public T* Query<T>(void* value) where T : unmanaged
        {
            ((IUnknown*)value)->QueryInterface(out T* result).ThrowOnFailure();
            return Own(result);
        }

        public BSTR Allocate(string value)
        {
            SysFreeStringSafeHandle text = PInvoke.SysAllocString(value);
            if (text.DangerousGetHandle() == 0)
            {
                text.Dispose();
                throw new OutOfMemoryException();
            }

            // This single-threaded scope retains the handle until all COM calls finish.
            _strings.Add(text);
            return (BSTR)text.DangerousGetHandle();
        }

        public void Dispose()
        {
            try
            {
                for (int i = _interfaces.Count - 1; i >= 0; i--)
                {
                    ((IUnknown*)_interfaces[i])->Release();
                }

                foreach (SysFreeStringSafeHandle text in _strings)
                {
                    text.Dispose();
                }
            }
            finally
            {
                PInvoke.CoUninitialize();
            }
        }
    }

    private static string Read(BSTR value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    public bool RebootRequired()
    {
        using ComScope com = new();
        ISystemInformation* info = com.CreateSystemInfo();
        return info->RebootRequired;
    }

    public WuaStatus Status()
    {
        using ComScope com = new();
        ISystemInformation* info = com.CreateSystemInfo();
        IUpdateSession* session = com.CreateSession();
        IUpdateInstaller* installer = com.Own(session->CreateUpdateInstaller());
        IUpdateServiceManager* manager = com.CreateServiceManager();
        IUpdateServiceCollection* services = com.Own(manager->Services);
        List<UpdateServiceInfo> result = [];
        for (int i = 0; i < Math.Min(services->Count, 100); i++)
        {
            IUpdateService* service = com.Own(services->get_Item(i));
            IUpdateService2* service2 = com.Query<IUpdateService2>(service);
            result.Add(new(Read(service->Name), Read(service->ServiceID), service->IsManaged,
                service2->IsDefaultAUService));
        }

        return new(info->RebootRequired, installer->IsBusy, result);
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
        IUpdateSession* session = com.CreateSession();
        session->ClientApplicationID = com.Allocate("Vestigo read-only diagnostics");
        IUpdateSearcher* searcher = com.Own(session->CreateUpdateSearcher());
        searcher->Online = false; // No API parameter can enable online scanning in this module.
        ISearchResult* result = com.Own(searcher->Search(com.Allocate(Criteria(state, drivers, hidden))));
        IUpdateCollection* updates = com.Own(result->Updates);
        int total = updates->Count;
        List<UpdateInfo> items = [];
        for (int i = 0; i < Math.Min(total, limit); i++)
        {
            IUpdate* update = com.Own(updates->get_Item(i));
            IUpdate2* update2 = com.Query<IUpdate2>(update);
            IUpdateIdentity* identity = com.Own(update->Identity);
            IStringCollection* kbs = com.Own(update->KBArticleIDs);
            List<string> references = [];
            for (int j = 0; j < Math.Min(kbs->Count, 100); j++)
            {
                references.Add(Read(kbs->get_Item(j)));
            }

            items.Add(new(Read(identity->UpdateID), identity->RevisionNumber, Read(update->Title), references,
                update->Type == UpdateType.utDriver ? "driver" : "software", update->IsInstalled, update->IsDownloaded,
                update->IsHidden, update2->RebootRequired));
        }

        IUpdateExceptionCollection* warnings = com.Own(result->Warnings);
        List<string> warningCodes = [];
        for (int i = 0; i < Math.Min(warnings->Count, 100); i++)
        {
            IUpdateException* warning = com.Own(warnings->get_Item(i));
            warningCodes.Add($"0x{warning->HResult:X8}");
        }

        return new((int)result->ResultCode, new(items, total, total > items.Count), warningCodes);
    }

    public BoundedList<UpdateHistoryEntry> History(int offset, int limit, bool failuresOnly)
    {
        using ComScope com = new();
        IUpdateSession* session = com.CreateSession();
        IUpdateSearcher* searcher = com.Own(session->CreateUpdateSearcher());
        int total = searcher->GetTotalHistoryCount();
        int count = Math.Min(limit, Math.Max(0, total - offset));
        if (count == 0)
        {
            return new([], 0, false);
        }

        IUpdateHistoryEntryCollection* history = com.Own(searcher->QueryHistory(offset, count));
        List<UpdateHistoryEntry> items = [];
        for (int i = 0; i < count; i++)
        {
            IUpdateHistoryEntry* entry = com.Own(history->get_Item(i));
            OperationResultCode code = entry->ResultCode;
            if (failuresOnly && code is not (OperationResultCode.orcSucceededWithErrors or OperationResultCode.orcFailed or OperationResultCode.orcAborted))
            {
                continue;
            }

            IUpdateIdentity* identity = com.Own(entry->UpdateIdentity);
            items.Add(new(DateTime.SpecifyKind(DateTime.FromOADate(entry->Date), DateTimeKind.Utc),
                Read(identity->UpdateID), identity->RevisionNumber, Read(entry->Title),
                (int)entry->Operation, (int)code, $"0x{entry->HResult:X8}"));
        }

        return new(items, count, (long)offset + count < total);
    }
}
