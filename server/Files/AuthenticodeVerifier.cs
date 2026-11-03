using Vestigo.Security.Signatures;

namespace Vestigo.Files;

/// <summary>
///     Embedded or catalog Authenticode check result. Status is Signed, Unsigned, Invalid, Untrusted, or Unavailable.
///     Source is embedded, catalog, or none. Unavailable is not reported as Unsigned.
/// </summary>
public sealed record SignatureInfo(string Status, string? HResult, string? Subject, string? Message, string? Source = null);

/// <summary>Maps a shared <see cref="SignatureVerification" /> onto the <c>stat_path</c> signature fields.</summary>
public static class AuthenticodeVerifier
{
    public static SignatureInfo ToSignatureInfo(SignatureVerification verification) =>
        new(
            SignatureOutcomeMap.ToStatus(verification.Outcome),
            verification.HResult,
            verification.Signer?.Subject,
            SignatureOutcomeMap.Message(verification.Outcome),
            verification.Source);

    /// <summary>Cache-only check used by tests. Does not contact the network.</summary>
    public static SignatureInfo Verify(string path) =>
        ToSignatureInfo(new WinTrustSignatureVerifier().Verify(path, RevocationMode.CacheOnly, includeSigner: true));
}
