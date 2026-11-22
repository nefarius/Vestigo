namespace Vestigo.SystemInfo;

/// <summary>Dispatches a one-shot logon to the in-process Credential Provider named-pipe host.</summary>
public interface IInteractiveLogonDispatcher
{
    bool LogonUiConnected { get; }

    bool ProviderInstalled { get; }

    bool ProviderRegistered { get; }

    Task<OperationResult<string>> DispatchAsync(
        string userName,
        string? domain,
        char[] password,
        string? expectedSid,
        bool requireUnlock,
        CancellationToken cancellationToken);
}
