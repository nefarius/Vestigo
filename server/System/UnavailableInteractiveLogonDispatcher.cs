namespace Vestigo.SystemInfo;

/// <summary>Used when interactive logon is not configured. Never talks to LogonUI.</summary>
public sealed class UnavailableInteractiveLogonDispatcher : IInteractiveLogonDispatcher
{
    public bool LogonUiConnected => false;

    public bool ProviderInstalled => File.Exists(
        CredentialProviderIdentity.StagingPath(AppContext.BaseDirectory));

    public bool ProviderRegistered => false;

    public Task<OperationResult<string>> DispatchAsync(
        string userName,
        string? domain,
        char[] password,
        string? expectedSid,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<string>.Fail(
            "interactive_logon_disabled",
            "Interactive logon is disabled. Set Vestigo:System:AllowInteractiveLogon to true and install the MSI Interactive logon feature."));
}
