namespace Vestigo.Diagnostics;

/// <summary>Runs <c>verifier.exe /query</c> and <c>verifier.exe /querysettings</c>. No other arguments are accepted.</summary>
internal sealed class WindowsVerifierQuery : IVerifierQuery
{
    public Task<VerifierCommandResult> QueryAsync(CancellationToken cancellationToken) =>
        VerifierInvocation.RunAsync(["/query"], cancellationToken);

    public Task<VerifierCommandResult> QuerySettingsAsync(CancellationToken cancellationToken) =>
        VerifierInvocation.RunAsync(["/querysettings"], cancellationToken);
}
