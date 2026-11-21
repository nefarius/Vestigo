namespace Vestigo.SystemInfo;

/// <summary>Observed Windows AutoLogon configuration. The password is never returned.</summary>
public sealed record AutoLogonState(
    bool Enabled,
    string? UserName,
    string? Domain,
    int? LogonCount,
    bool ForceAutoLogon,
    bool LsaSecretPresent,
    bool LegacyPlaintextPassword,
    bool? PasswordlessDevice,
    IReadOnlyList<string> Notes);

/// <summary>Result of enabling or disabling AutoLogon.</summary>
public sealed record AutoLogonChange(
    bool Enabled,
    string? UserName,
    string? Domain,
    int? LogonCount,
    bool ForceAutoLogon,
    IReadOnlyList<string> Notes);

/// <summary>Winlogon AutoLogon plus the LSA <c>DefaultPassword</c> private secret. Tests supply a fake.</summary>
public interface IAutoLogonPlatform
{
    AutoLogonState Read();

    OperationResult<AutoLogonChange> Enable(
        string userName,
        string? domain,
        ReadOnlySpan<char> password,
        int? logonCount,
        bool forceAutoLogon);

    OperationResult<AutoLogonChange> Disable(bool clearDefaultUser);

    /// <summary>Verifies the account with <c>LogonUser</c> and immediately closes the token.</summary>
    OperationResult<bool> VerifyCredentials(string userName, string? domain, ReadOnlySpan<char> password);
}
