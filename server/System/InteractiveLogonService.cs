using Microsoft.Extensions.Logging;

using Vestigo.Configuration;

namespace Vestigo.SystemInfo;

/// <summary>Issues consent links for lock/logon-screen sign-in and dispatches approved credentials to LogonUI.</summary>
public sealed class InteractiveLogonService(
    IInteractiveLogonDispatcher dispatcher,
    ISessionPlatform sessions,
    IAutoLogonPlatform credentials,
    LogonConsentStore store,
    VestigoOptions options,
    ILogger<InteractiveLogonService> logger,
    IHttpContextAccessor? httpContext = null)
{
    public OperationResult<InteractiveLogonStatus> GetStatus()
    {
        ConsoleLockRead lockState = sessions.ReadConsoleLock();
        List<string> notes = [];
        if (!options.System.AllowInteractiveLogon)
        {
            notes.Add("Set Vestigo:System:AllowInteractiveLogon to true.");
        }

        if (!dispatcher.ProviderInstalled)
        {
            notes.Add("The MSI Interactive logon feature is not installed.");
        }
        else if (!dispatcher.ProviderRegistered)
        {
            notes.Add("The Credential Provider DLL is present but not registered for LogonUI.");
        }

        if (options.System.AllowInteractiveLogon && dispatcher.ProviderRegistered && !dispatcher.LogonUiConnected)
        {
            notes.Add("LogonUI is not connected. Interactive logon works from the Windows logon or lock screen.");
        }

        if (lockState.Success)
        {
            notes.Add($"Console session state is {lockState.State}.");
        }
        else
        {
            notes.Add(lockState.Error ?? "Console lock state could not be read.");
        }

        notes.Add("Passwords are never returned.");
        return OperationResult<InteractiveLogonStatus>.Ok(new InteractiveLogonStatus(
            options.System.AllowInteractiveLogon,
            dispatcher.ProviderInstalled,
            dispatcher.ProviderRegistered,
            dispatcher.LogonUiConnected,
            lockState.Success ? lockState.State : "unknown",
            lockState.SessionId,
            lockState.UserName,
            notes));
    }

    public OperationResult<LogonConsentLink> RequestLogon(string? userName, string? domain)
    {
        if (!options.System.AllowInteractiveLogon)
        {
            return OperationResult<LogonConsentLink>.Fail(
                "interactive_logon_disabled",
                "Interactive logon is disabled. Set Vestigo:System:AllowInteractiveLogon to true.");
        }

        if (!dispatcher.ProviderRegistered)
        {
            return OperationResult<LogonConsentLink>.Fail(
                "provider_not_registered",
                "The Vestigo Credential Provider is not registered. Enable the MSI Interactive logon feature.");
        }

        if (!LogonAccount.TryNormalize(userName, domain, out string user, out string? parsedDomain, out string nameError))
        {
            return OperationResult<LogonConsentLink>.Fail("invalid_request", nameError);
        }

        ConsoleLockRead lockState = sessions.ReadConsoleLock();
        if (!lockState.Success)
        {
            return OperationResult<LogonConsentLink>.Fail(lockState.ErrorCode!, lockState.Error!);
        }

        if (!lockState.CanInteractiveLogon)
        {
            return OperationResult<LogonConsentLink>.Fail(
                lockState.State == "unlocked" ? "already_unlocked" : "lock_state_unknown",
                lockState.State == "unlocked"
                    ? "The console session is already unlocked."
                    : "The console lock state could not be determined.");
        }

        if (!dispatcher.LogonUiConnected)
        {
            return OperationResult<LogonConsentLink>.Fail(
                "logonui_not_connected",
                "LogonUI is not connected. Interactive logon works from the Windows logon or lock screen.");
        }

        OperationResult<LogonConsentLink> issued = store.Issue(
            LogonConsentKind.InteractiveLogon,
            user,
            parsedDomain,
            logonCount: null,
            forceAutoLogon: false,
            PublicBaseUrl());
        logger.LogInformation(
            "request_interactive_logon user {User} domain {Domain} lock {Lock} success {Success} code {Code}",
            user,
            string.IsNullOrEmpty(parsedDomain) ? "(local)" : parsedDomain,
            lockState.State,
            issued.Success,
            issued.ErrorCode);
        return issued;
    }

    public async Task<OperationResult<LogonRequestView>> ApproveAsync(
        LogonConsentStore.PendingConsent pending,
        char[] password,
        CancellationToken cancellationToken)
    {
        OperationResult<bool> verified = credentials.VerifyCredentials(pending.UserName, pending.Domain, password);
        if (!verified.Success)
        {
            if (verified.ErrorCode == "invalid_credentials")
            {
                store.RecordFailure(pending.RequestId, out bool locked);
                if (locked)
                {
                    return OperationResult<LogonRequestView>.Fail(
                        "too_many_attempts",
                        "Too many incorrect passwords.");
                }
            }
            else
            {
                store.Complete(pending.RequestId, LogonRequestStatus.Failed, verified.ErrorCode, verified.Error);
            }

            return OperationResult<LogonRequestView>.Fail(verified.ErrorCode!, verified.Error!);
        }

        store.Complete(pending.RequestId, LogonRequestStatus.Submitted, null, null);
        OperationResult<string> dispatched;
        try
        {
            dispatched = await dispatcher
                .DispatchAsync(pending.UserName, pending.Domain, password, expectedSid: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            store.Complete(
                pending.RequestId,
                LogonRequestStatus.Failed,
                "logon_failed",
                "The Credential Provider dispatch failed before LogonUI accepted the credentials.");
            throw;
        }

        if (!dispatched.Success)
        {
            store.Complete(pending.RequestId, LogonRequestStatus.Failed, dispatched.ErrorCode, dispatched.Error);
            return OperationResult<LogonRequestView>.Fail(dispatched.ErrorCode!, dispatched.Error!);
        }

        store.Complete(pending.RequestId, LogonRequestStatus.Approved, null, null);
        logger.LogInformation("interactive_logon submitted request {RequestId}", pending.RequestId);
        return OperationResult<LogonRequestView>.Ok(store.Peek(pending.RequestId)!);
    }

    internal string PublicBaseUrl()
    {
        HttpRequest? request = httpContext?.HttpContext?.Request;
        if (request is not null && request.Host.HasValue)
        {
            return $"{request.Scheme}://{request.Host.Value}";
        }

        System.Net.IPAddress address = System.Net.IPAddress.Parse(options.ListenAddress);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{options.ListenPort}";
    }
}
