using Microsoft.Extensions.Logging;

using Vestigo.Configuration;

namespace Vestigo.SystemInfo;

/// <summary>Validates AutoLogon requests, issues consent links, and applies approved credentials.</summary>
public sealed class AutoLogonService(
    IAutoLogonPlatform platform,
    LogonConsentStore store,
    VestigoOptions options,
    ILogger<AutoLogonService> logger,
    IHttpContextAccessor? httpContext = null)
{
    public OperationResult<AutoLogonState> GetState() => OperationResult<AutoLogonState>.Ok(platform.Read());

    public OperationResult<LogonConsentLink> RequestEnable(
        string? userName,
        string? domain,
        int? logonCount,
        bool forceAutoLogon)
    {
        if (!options.System.AllowAutoLogonChanges)
        {
            return OperationResult<LogonConsentLink>.Fail(
                "autologon_disabled",
                "Changing AutoLogon is disabled. Set Vestigo:System:AllowAutoLogonChanges to true.");
        }

        if (!LogonAccount.TryNormalize(userName, domain, out string user, out string? parsedDomain, out string nameError))
        {
            return OperationResult<LogonConsentLink>.Fail("invalid_request", nameError);
        }

        if (!LogonAccount.TryNormalizeLogonCount(logonCount, out int? count, out string countError))
        {
            return OperationResult<LogonConsentLink>.Fail("invalid_request", countError);
        }

        OperationResult<LogonConsentLink> issued = store.Issue(
            LogonConsentKind.AutoLogonEnable,
            user,
            parsedDomain,
            count,
            forceAutoLogon,
            PublicBaseUrl());
        logger.LogInformation(
            "request_autologon_enable user {User} domain {Domain} count {Count} force {Force} success {Success} code {Code}",
            user,
            string.IsNullOrEmpty(parsedDomain) ? "(local)" : parsedDomain,
            count,
            forceAutoLogon,
            issued.Success,
            issued.ErrorCode);
        return issued;
    }

    public OperationResult<AutoLogonDisableResult> Disable(bool clearDefaultUser)
    {
        if (!options.System.AllowAutoLogonChanges)
        {
            return OperationResult<AutoLogonDisableResult>.Fail(
                "autologon_disabled",
                "Changing AutoLogon is disabled. Set Vestigo:System:AllowAutoLogonChanges to true.");
        }

        OperationResult<AutoLogonChange> changed = platform.Disable(clearDefaultUser);
        logger.LogInformation(
            "disable_autologon clearUser {Clear} success {Success} code {Code}",
            clearDefaultUser,
            changed.Success,
            changed.ErrorCode);
        if (!changed.Success)
        {
            return changed.FailAs<AutoLogonDisableResult>();
        }

        return OperationResult<AutoLogonDisableResult>.Ok(new AutoLogonDisableResult(
            changed.Value!.Enabled,
            changed.Value.UserName,
            changed.Value.Domain,
            changed.Value.Notes));
    }

    public OperationResult<LogonRequestView> Approve(LogonConsentStore.PendingConsent pending, ReadOnlySpan<char> password)
    {
        OperationResult<AutoLogonChange> changed = platform.Enable(
            pending.UserName,
            pending.Domain,
            password,
            pending.LogonCount,
            pending.ForceAutoLogon);
        if (!changed.Success)
        {
            if (changed.ErrorCode == "invalid_credentials")
            {
                store.RecordFailure(pending.RequestId, out bool locked);
                if (locked)
                {
                    return OperationResult<LogonRequestView>.Fail(
                        "too_many_attempts",
                        "Too many incorrect passwords.");
                }

                return OperationResult<LogonRequestView>.Fail(changed.ErrorCode, changed.Error!);
            }

            store.Complete(pending.RequestId, LogonRequestStatus.Failed, changed.ErrorCode, changed.Error);
            return OperationResult<LogonRequestView>.Fail(changed.ErrorCode!, changed.Error!);
        }

        store.Complete(pending.RequestId, LogonRequestStatus.Approved, null, null);
        logger.LogInformation("autologon_enable approved request {RequestId}", pending.RequestId);
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
