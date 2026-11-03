using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.EventLogs;
using Vestigo.Services;
using Vestigo.SystemInfo;

namespace Vestigo.Diagnostics;

/// <summary>Collects read-only evidence and runs <see cref="DeviceProblemRules" />. Missing optional modules stay unavailable.</summary>
internal sealed class DeviceEvidenceGatherer(
    IDeviceControl devices,
    ISetupApiLogSource logs,
    VestigoOptions options,
    IServiceControl? services = null,
    SystemService? system = null,
    EventLogService? events = null) : IDeviceProblemSource
{
    public OperationResult<DeviceProblemReport> Explain(string instanceId)
    {
        if (!InstallHistoryTools.TryInstanceId(instanceId, out string id, out string? error))
        {
            return OperationResult<DeviceProblemReport>.Fail("invalid_request", error!);
        }

        DeviceDetails device;
        try
        {
            device = devices.GetDevice(id);
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<DeviceProblemReport>.Fail(ex.Code, ex.Message);
        }

        ClassFilterSet? filters = null;
        string? filterNote = null;
        if (device.ClassGuid is Guid classGuid)
        {
            try
            {
                filters = devices.GetClassFilters(classGuid);
            }
            catch (DeviceControlException ex)
            {
                filterNote = ex.Code;
            }
        }

        ServiceInfo? service = null;
        string? serviceNote = null;
        if (!string.IsNullOrWhiteSpace(device.Service))
        {
            if (services is null)
            {
                serviceNote = "unavailable";
            }
            else
            {
                try
                {
                    service = services.Get(device.Service);
                    serviceNote = service is null ? "not_found" : null;
                }
                catch (ServiceControlException ex)
                {
                    serviceNote = ex.Code;
                }
            }
        }

        BootSecurityResult? boot = null;
        string? bootNote = null;
        if (device.ProblemCode == 52)
        {
            if (system is null)
            {
                bootNote = "unavailable";
            }
            else
            {
                OperationResult<BootSecurityResult> security = system.GetBootSecurity();
                if (!security.Success)
                {
                    bootNote = security.ErrorCode;
                }
                else
                {
                    boot = security.Value;
                }
            }
        }

        IReadOnlyList<EventEntry> pnp = [];
        string? pnpNote = null;
        if (events is null)
        {
            pnpNote = "unavailable";
        }
        else
        {
            OperationResult<EventQueryResult> query = events.Query(
                "Microsoft-Windows-Kernel-PnP/Configuration",
                new EventLogFilter(),
                null,
                id,
                10,
                true,
                false,
                CancellationToken.None);
            if (!query.Success)
            {
                pnpNote = query.ErrorCode;
            }
            else
            {
                pnp = query.Value?.Events ?? [];
            }
        }

        InstallHistory? history = null;
        string? installNote = null;
        OperationResult<InstallHistory> install = logs.Read(id, null, 5, false, options.Diagnostics.MaxInstallHistoryBytes);
        if (!install.Success)
        {
            installNote = install.ErrorCode;
        }
        else
        {
            history = install.Value;
        }

        DeviceProblemEvidence evidence = new(device, filters, filterNote, service, serviceNote, boot, bootNote, pnp, pnpNote, history, installNote);
        return OperationResult<DeviceProblemReport>.Ok(DeviceProblemRules.Assess(evidence));
    }
}
