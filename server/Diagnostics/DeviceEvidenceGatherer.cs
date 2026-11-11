using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.EventLogs;
using Vestigo.Security.Signatures;
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
    EventLogService? events = null,
    ISignatureVerifier? signatures = null,
    ICodeIntegrityProbe? integrity = null) : IDeviceProblemSource
{
    private const string CodeIntegrityChannel = "Microsoft-Windows-CodeIntegrity/Operational";

    public OperationResult<DeviceProblemReport> Explain(string instanceId)
    {
        if (!InstallHistoryTools.TryInstanceId(instanceId, out _, out string? idError))
        {
            return OperationResult<DeviceProblemReport>.Fail("invalid_request", idError!);
        }

        OperationResult<DeviceProblemEvidence> evidence = Collect(instanceId, null, false, out _);
        if (!evidence.Success)
        {
            return evidence.FailAs<DeviceProblemReport>();
        }

        return OperationResult<DeviceProblemReport>.Ok(DeviceProblemRules.Assess(evidence.Value!));
    }

    public OperationResult<DriverLoadReport> ExplainLoad(string? instanceId, string? serviceName)
    {
        bool hasInstance = !string.IsNullOrWhiteSpace(instanceId);
        bool hasService = !string.IsNullOrWhiteSpace(serviceName);
        if (!hasInstance && !hasService)
        {
            return OperationResult<DriverLoadReport>.Fail("invalid_request", "instanceId or serviceName is required.");
        }

        string parsedService = "";
        if (hasService && !TryServiceName(serviceName!, out parsedService, out string? serviceError))
        {
            return OperationResult<DriverLoadReport>.Fail("invalid_request", serviceError!);
        }

        OperationResult<DeviceProblemEvidence> evidence = Collect(hasInstance ? instanceId : null, hasService ? parsedService : null, true, out string? imageFileName);
        if (!evidence.Success)
        {
            return evidence.FailAs<DriverLoadReport>();
        }

        DeviceProblemEvidence collected = evidence.Value!;
        if (hasInstance && hasService &&
            collected.Device?.Service is { } deviceService &&
            !string.Equals(deviceService, parsedService, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<DriverLoadReport>.Fail("service_mismatch", "serviceName does not match the service named by the device.");
        }

        string? resolvedService = hasService ? parsedService : collected.Device?.Service;
        collected = AddLoadEvidence(collected, imageFileName);
        return OperationResult<DriverLoadReport>.Ok(DeviceProblemRules.ExplainLoad(collected, resolvedService, imageFileName));
    }

    private OperationResult<DeviceProblemEvidence> Collect(string? instanceId, string? serviceName, bool alwaysBoot, out string? imageFileName)
    {
        imageFileName = null;
        DeviceDetails? device = null;
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            if (!InstallHistoryTools.TryInstanceId(instanceId, out string id, out string? error))
            {
                return OperationResult<DeviceProblemEvidence>.Fail("invalid_request", error!);
            }

            try
            {
                device = devices.GetDevice(id);
            }
            catch (DeviceControlException ex)
            {
                return OperationResult<DeviceProblemEvidence>.Fail(ex.Code, ex.Message);
            }
        }

        ClassFilterSet? filters = null;
        string? filterNote = null;
        if (device?.ClassGuid is Guid classGuid)
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

        string? resolvedService = serviceName ?? device?.Service;
        ServiceInfo? service = null;
        string? serviceNote = null;
        if (!string.IsNullOrWhiteSpace(resolvedService))
        {
            if (services is null)
            {
                serviceNote = "unavailable";
            }
            else
            {
                try
                {
                    service = services.Get(resolvedService);
                    serviceNote = service is null ? "not_found" : null;
                }
                catch (ServiceControlException ex)
                {
                    serviceNote = ex.Code;
                }
            }
        }

        imageFileName = CodeIntegrityProjection.ImageFileName(service?.ImagePath);
        BootSecurityResult? boot = null;
        string? bootNote = null;
        if (alwaysBoot || device?.ProblemCode == 52)
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
        if (device is not null)
        {
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
                    device.InstanceId,
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
        }

        InstallHistory? history = null;
        string? installNote = null;
        if (device is not null)
        {
            OperationResult<InstallHistory> install = logs.Read(device.InstanceId, null, 5, false, options.Diagnostics.MaxInstallHistoryBytes);
            if (!install.Success)
            {
                installNote = install.ErrorCode;
            }
            else
            {
                history = install.Value;
            }
        }

        return OperationResult<DeviceProblemEvidence>.Ok(
            new DeviceProblemEvidence(device, filters, filterNote, service, serviceNote, boot, bootNote, pnp, pnpNote, history, installNote));
    }

    private DeviceProblemEvidence AddLoadEvidence(DeviceProblemEvidence evidence, string? imageFileName)
    {
        CodeIntegrityPresence? presence = null;
        string? hvciNote = null;
        string? lockdownNote = null;
        if (integrity is null)
        {
            hvciNote = "unavailable";
            lockdownNote = "unavailable";
        }
        else
        {
            try
            {
                presence = integrity.Read();
                hvciNote = presence.HvciNote;
                lockdownNote = presence.LockdownNote;
            }
            catch (Exception)
            {
                hvciNote = "unavailable";
                lockdownNote = "unavailable";
            }
        }

        SignatureVerification? signature = null;
        string? signatureNote = null;
        string? image = CodeIntegrityProjection.ExpandImagePath(evidence.Service?.ImagePath);
        if (imageFileName is null)
        {
            signatureNote = evidence.Service?.ImagePath is null ? "no_image" : "unparsed";
        }
        else if (signatures is null)
        {
            signatureNote = "unavailable";
        }
        else if (image is null || !File.Exists(image))
        {
            signatureNote = "not_found";
        }
        else
        {
            try
            {
                signature = signatures.Verify(image, RevocationMode.CacheOnly, includeSigner: false);
            }
            catch (Exception)
            {
                signatureNote = "unavailable";
            }
        }

        IReadOnlyList<CodeIntegrityEvent> codeEvents = [];
        string? eventNote = null;
        if (imageFileName is null)
        {
            eventNote = "no_image";
        }
        else if (events is null)
        {
            eventNote = "unavailable";
        }
        else
        {
            OperationResult<EventQueryResult> query = events.Query(
                CodeIntegrityChannel,
                new EventLogFilter(),
                null,
                imageFileName,
                20,
                true,
                true,
                CancellationToken.None);
            if (!query.Success)
            {
                eventNote = query.ErrorCode;
            }
            else
            {
                codeEvents = ProjectEvents(query.Value?.Events, imageFileName);
                if (query.Value?.Truncated == true || query.Value?.ScanLimitReached == true)
                {
                    eventNote = "truncated";
                }
            }
        }

        return evidence with
        {
            Signature = signature,
            SignatureNote = signatureNote,
            HvciEnabledValue = presence?.HvciEnabledValue,
            HvciEnabled = presence?.HvciEnabled,
            HvciNote = hvciNote,
            WindowsLockdownMode = presence?.WindowsLockdownMode,
            WindowsLockdownModeName = presence?.WindowsLockdownModeName,
            LockdownNote = lockdownNote,
            CodeIntegrityEvents = codeEvents,
            CodeIntegrityEventNote = eventNote
        };
    }

    private static List<CodeIntegrityEvent> ProjectEvents(IReadOnlyList<EventEntry>? entries, string imageFileName)
    {
        List<CodeIntegrityEvent> matched = [];
        if (entries is null)
        {
            return matched;
        }

        foreach (EventEntry entry in entries)
        {
            CodeIntegrityEvent? parsed = CodeIntegrityProjection.ParseEvent(entry.Xml, entry.Id, entry.Time, entry.Message, imageFileName);
            if (parsed is not null)
            {
                matched.Add(parsed);
            }
        }

        return matched;
    }

    private static bool TryServiceName(string name, out string parsed, out string? error)
    {
        parsed = name.Trim();
        if (parsed.Length is < 1 or > 256 || parsed.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            error = "serviceName must be a service name without a path.";
            parsed = "";
            return false;
        }

        error = null;
        return true;
    }
}
