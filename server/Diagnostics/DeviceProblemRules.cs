using Vestigo.Devices;
using Vestigo.EventLogs;
using Vestigo.Services;

namespace Vestigo.Diagnostics;

/// <summary>
///     Documented CM_PROB meanings. Hypotheses cite evidence already gathered and do not rank drivers.
/// </summary>
internal static class DeviceProblemRules
{
    private static readonly IReadOnlyDictionary<uint, string> Meanings = new Dictionary<uint, string>
    {
        [1] = "CM_PROB_NOT_CONFIGURED (1): the device is not configured correctly.",
        [10] = "CM_PROB_FAILED_START (10): the device cannot start.",
        [18] = "CM_PROB_REINSTALL (18): the drivers for this device need to be reinstalled.",
        [19] = "CM_PROB_REGISTRY (19): the device's configuration information in the registry is incomplete or damaged.",
        [22] = "CM_PROB_DISABLED (22): the device is disabled.",
        [24] = "CM_PROB_DEVICE_NOT_THERE (24): the device is not present, is not working properly, or does not have all of its drivers installed.",
        [28] = "CM_PROB_FAILED_INSTALL (28): the drivers for this device are not installed.",
        [31] = "CM_PROB_FAILED_ADD (31): Windows cannot load the drivers required for this device.",
        [37] = "CM_PROB_FAILED_DRIVER_ENTRY (37): Windows cannot initialize the device driver for this hardware.",
        [39] = "CM_PROB_DRIVER_FAILED_LOAD (39): Windows cannot load the device driver for this hardware. The driver may be corrupted or missing.",
        [43] = "CM_PROB_FAILED_POST_START (43): Windows stopped this device because it reported problems.",
        [45] = "CM_PROB_PHANTOM (45): this hardware device is not connected to the computer.",
        [52] = "CM_PROB_UNSIGNED_DRIVER (52): Windows cannot verify the digital signature for the drivers required for this device."
    };

    private static readonly HashSet<uint> ServiceCodes = [10, 19, 31, 37, 39];

    public static DeviceProblemReport Assess(DeviceProblemEvidence evidence)
    {
        if (evidence.Device is not { } device)
        {
            return new DeviceProblemReport("", null, [], null, [], true);
        }

        List<ObservedFact> observed = Facts(evidence);
        uint? code = device.ProblemCode;
        if (code is null)
        {
            return new DeviceProblemReport(device.InstanceId, null, observed, null, [], true);
        }

        if (code == 0)
        {
            return new DeviceProblemReport(device.InstanceId, code, observed, "No CM_PROB code is set on this device.", [], false);
        }

        if (!Meanings.TryGetValue(code.Value, out string? explanation))
        {
            return new DeviceProblemReport(device.InstanceId, code, observed, null, [], true);
        }

        List<ProblemHypothesis> hypotheses = [];
        if (ServiceCodes.Contains(code.Value))
        {
            AddService(evidence, hypotheses, null);
        }

        if (code is 18 or 28 or 31)
        {
            AddInstall(evidence, hypotheses);
        }

        if (code == 52)
        {
            AddBoot(evidence, hypotheses);
        }

        if (code == 43)
        {
            AddPnp(evidence, hypotheses);
        }

        return new DeviceProblemReport(device.InstanceId, code, observed, explanation, hypotheses, false);
    }

    private static readonly uint[] LoadCodes = [10, 31, 37, 39, 52];

    /// <summary>
    ///     Load report. A hypothesis is added only when an observation supports it.
    ///     HVCI and Windows lockdown mode are facts, not causes.
    /// </summary>
    public static DriverLoadReport ExplainLoad(DeviceProblemEvidence evidence, string? serviceName, string? imageFileName)
    {
        List<ObservedFact> observed = [];
        List<ProblemHypothesis> hypotheses = [];
        string? explanation = null;
        uint? code = evidence.Device?.ProblemCode;
        bool loadCode = code is not null && LoadCodes.Contains(code.Value);
        if (evidence.Device is not null)
        {
            DeviceProblemReport deviceReport = Assess(evidence);
            observed.AddRange(deviceReport.Observed);
            if (loadCode)
            {
                explanation = deviceReport.Explanation;
                hypotheses.AddRange(deviceReport.Hypotheses);
            }
        }
        else
        {
            AddLooseService(evidence, serviceName, observed);
            AddLooseBoot(evidence, observed);
            AddService(evidence, hypotheses, serviceName);
        }

        observed.Add(new ObservedFact(
            "code-integrity",
            $"hvciEnabledValue={evidence.HvciEnabledValue?.ToString() ?? "null"}; hvciEnabled={evidence.HvciEnabled?.ToString() ?? "null"}; windowsLockdownMode={evidence.WindowsLockdownMode?.ToString() ?? "null"}; windowsLockdownModeName={evidence.WindowsLockdownModeName ?? "null"}",
            null));
        if (evidence.HvciNote is not null || evidence.LockdownNote is not null)
        {
            observed.Add(new ObservedFact("code-integrity", $"hvci={evidence.HvciNote ?? "ok"}; lockdown={evidence.LockdownNote ?? "ok"}", null));
        }

        if (imageFileName is not null)
        {
            observed.Add(new ObservedFact("image", $"file={imageFileName}", null));
        }

        if (evidence.Signature is { } signature)
        {
            observed.Add(new ObservedFact("signature", $"outcome={signature.Outcome}; source={signature.Source}; revocation={signature.RevocationMode}", signature.ObservedAt));
        }
        else if (evidence.SignatureNote is not null)
        {
            observed.Add(new ObservedFact("signature", evidence.SignatureNote, null));
        }

        bool matched = false;
        foreach (CodeIntegrityEvent entry in evidence.CodeIntegrityEvents)
        {
            if (!CodeIntegrityProjection.NamesFile(entry, imageFileName))
            {
                continue;
            }

            matched = true;
            observed.Add(new ObservedFact(
                "code-integrity-event",
                $"id={entry.Id}; file={entry.FileName ?? imageFileName}; requestedPolicy={entry.RequestedPolicy ?? "null"}; status={entry.Status ?? "null"}",
                entry.At));
            hypotheses.Add(new ProblemHypothesis(
                "code-integrity",
                $"Microsoft-Windows-CodeIntegrity event {entry.Id} names '{imageFileName}'.",
                [$"code-integrity:{entry.Id}"]));
        }

        if (evidence.CodeIntegrityEventNote is not null)
        {
            observed.Add(new ObservedFact("code-integrity-event", evidence.CodeIntegrityEventNote, null));
        }

        if (evidence.Signature?.Outcome is "unsigned" or "invalid" or "untrusted" && (loadCode || matched))
        {
            hypotheses.Add(new ProblemHypothesis(
                "image-signature",
                $"The service image signature outcome is {evidence.Signature.Outcome}.",
                [$"signature:{evidence.Signature.Outcome}"]));
        }

        bool serviceHit = hypotheses.Exists(item => item.Label == "service-state");
        bool insufficient = !loadCode && !matched && !serviceHit;
        return new DriverLoadReport(
            evidence.Device?.InstanceId,
            serviceName ?? evidence.Device?.Service,
            imageFileName,
            code,
            observed,
            insufficient ? null : explanation,
            hypotheses,
            insufficient);
    }

    private static void AddLooseService(DeviceProblemEvidence evidence, string? serviceName, List<ObservedFact> observed)
    {
        if (evidence.Service is { } service)
        {
            observed.Add(new ObservedFact("service", $"{service.Name} state={service.State}; startType={service.StartType ?? "null"}", null));
        }
        else if (evidence.ServiceNote is not null)
        {
            observed.Add(new ObservedFact("service", serviceName is null ? evidence.ServiceNote : $"{evidence.ServiceNote}:{serviceName}", null));
        }
    }

    private static void AddLooseBoot(DeviceProblemEvidence evidence, List<ObservedFact> observed)
    {
        if (evidence.BootSecurity is { } boot)
        {
            observed.Add(new ObservedFact(
                "boot-security",
                $"secureBoot={boot.SecureBoot?.ToString() ?? "null"}; testSigningActive={boot.TestSigningActive?.ToString() ?? "null"}; testSigningConfigured={boot.TestSigningConfigured?.ToString() ?? "null"}",
                null));
        }
        else if (evidence.BootSecurityNote is not null)
        {
            observed.Add(new ObservedFact("boot-security", evidence.BootSecurityNote, null));
        }
    }

    private static List<ObservedFact> Facts(DeviceProblemEvidence evidence)
    {
        DeviceDetails device = evidence.Device!;
        List<ObservedFact> facts =
        [
            new("device", $"problemCode={device.ProblemCode?.ToString() ?? "null"}; devNodeStatus={device.DevNodeStatus?.ToString() ?? "null"}; present={device.IsPresent?.ToString() ?? "null"}", null),
            new("device", $"class={device.ClassName ?? "null"}; service={device.Service ?? "null"}; inf={Path.GetFileName(device.DriverInfPath ?? "")}; driverVersion={device.DriverVersion ?? "null"}", device.DriverDate)
        ];
        if (evidence.ClassFilters is { } filters)
        {
            facts.Add(new ObservedFact("class-filters", $"upper={Join(filters.Upper)}; lower={Join(filters.Lower)}", null));
        }
        else if (evidence.ClassFilterNote is not null)
        {
            facts.Add(new ObservedFact("class-filters", evidence.ClassFilterNote, null));
        }

        if (evidence.Service is { } service)
        {
            facts.Add(new ObservedFact("service", $"{service.Name} state={service.State}; startType={service.StartType ?? "null"}", null));
        }
        else if (evidence.ServiceNote is not null)
        {
            facts.Add(new ObservedFact("service", evidence.ServiceNote, null));
        }

        if (evidence.BootSecurity is { } boot)
        {
            facts.Add(new ObservedFact(
                "boot-security",
                $"secureBoot={boot.SecureBoot?.ToString() ?? "null"}; testSigningActive={boot.TestSigningActive?.ToString() ?? "null"}; testSigningConfigured={boot.TestSigningConfigured?.ToString() ?? "null"}",
                null));
        }
        else if (evidence.BootSecurityNote is not null)
        {
            facts.Add(new ObservedFact("boot-security", evidence.BootSecurityNote, null));
        }

        foreach (EventEntry entry in evidence.PnpEvents)
        {
            string message = entry.Message ?? "";
            if (message.Length > 240)
            {
                message = message[..240];
            }

            facts.Add(new ObservedFact("kernel-pnp", $"id={entry.Id}; {message}", entry.Time));
        }

        if (evidence.PnpNote is not null)
        {
            facts.Add(new ObservedFact("kernel-pnp", evidence.PnpNote, null));
        }

        if (evidence.InstallHistory is { } history)
        {
            foreach (InstallSection section in history.Sections)
            {
                facts.Add(new ObservedFact(
                    "setupapi",
                    $"{section.SourceFile} exit={section.ExitStatus ?? "null"}; complete={section.Complete}",
                    section.EndedAt ?? section.StartedAt));
            }

            if (history.Truncated)
            {
                facts.Add(new ObservedFact("setupapi", "truncated", null));
            }
        }
        else if (evidence.InstallNote is not null)
        {
            facts.Add(new ObservedFact("setupapi", evidence.InstallNote, null));
        }

        return facts;
    }

    private static void AddService(DeviceProblemEvidence evidence, List<ProblemHypothesis> hypotheses, string? namedService)
    {
        if (evidence.Service is not { } service)
        {
            string? name = evidence.Device?.Service ?? namedService;
            if (evidence.ServiceNote == "not_found" && name is not null)
            {
                string named = evidence.Device is null ? "was not found" : "named by the device was not found";
                hypotheses.Add(new ProblemHypothesis("service-state", $"The service '{name}' {named}.", [$"service:{name}"]));
            }

            return;
        }

        if (service.State == ServiceStates.Stopped || service.StartType == "disabled")
        {
            hypotheses.Add(new ProblemHypothesis(
                "service-state",
                $"The service '{service.Name}' named by the device is {service.State} (start type {service.StartType ?? "unknown"}).",
                [$"service:{service.Name}"]));
        }
    }

    private static void AddInstall(DeviceProblemEvidence evidence, List<ProblemHypothesis> hypotheses)
    {
        InstallSection? latest = evidence.InstallHistory?.Sections.LastOrDefault(section => section.ExitStatus is not null);
        if (latest?.ExitStatus is not { } exit || exit.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        hypotheses.Add(new ProblemHypothesis(
            "install-exit",
            $"The latest matching install section in {latest.SourceFile} ended with {exit}.",
            [$"setupapi:{latest.SourceFile}"]));
    }

    private static void AddBoot(DeviceProblemEvidence evidence, List<ProblemHypothesis> hypotheses)
    {
        if (evidence.BootSecurity?.TestSigningActive == false)
        {
            hypotheses.Add(new ProblemHypothesis(
                "boot-security",
                "Test signing is not active, so an unsigned driver stays subject to signature enforcement.",
                ["boot-security:testSigningActive"]));
        }
    }

    private static void AddPnp(DeviceProblemEvidence evidence, List<ProblemHypothesis> hypotheses)
    {
        EventEntry? latest = evidence.PnpEvents.FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        hypotheses.Add(new ProblemHypothesis(
            "kernel-pnp",
            $"Kernel-PnP configuration event {latest.Id} was recorded for this device.",
            [$"kernel-pnp:{latest.Id}"]));
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);
}
