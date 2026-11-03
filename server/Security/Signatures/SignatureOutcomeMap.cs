namespace Vestigo.Security.Signatures;

/// <summary>Maps WinVerifyTrust results onto the public outcome names.</summary>
public static class SignatureOutcomeMap
{
    public const int TrustENoSignature = unchecked((int)0x800B0100);

    public const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    public const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    public const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);

    public const int CertEExpired = unchecked((int)0x800B0101);

    public const int CertEUntrustedRoot = unchecked((int)0x800B0109);

    public const int CertEChaining = unchecked((int)0x800B010A);

    public const int CertERevoked = unchecked((int)0x800B010C);

    public const int CertEUntrustedTestRoot = unchecked((int)0x800B010D);

    public const int CertERevocationFailure = unchecked((int)0x800B010E);

    public const int CertEWrongUsage = unchecked((int)0x800B0110);

    public const int TrustEExplicitDistrust = unchecked((int)0x800B0111);

    public const int TrustEBadDigest = unchecked((int)0x80096010);

    public const int CryptERevocationOffline = unchecked((int)0x80092013);

    public const int CryptENoRevocationCheck = unchecked((int)0x80092012);

    public const string Unsigned = "unsigned";

    public const string Valid = "valid";

    public const string Invalid = "invalid";

    public const string Untrusted = "untrusted";

    public const string Unavailable = "unavailable";

    public const string SourceEmbedded = "embedded";

    public const string SourceCatalog = "catalog";

    public const string SourceNone = "none";

    /// <summary>
    ///     Maps an HRESULT. <paramref name="signaturePresent" /> is false only when neither an embedded signature
    ///     nor a catalog was found. An unavailable result is never reported as unsigned.
    /// </summary>
    public static string Map(int hresult, bool signaturePresent)
    {
        if (hresult == 0)
        {
            return Valid;
        }

        if (IsRevocationUnavailable(hresult))
        {
            return Unavailable;
        }

        if (!signaturePresent && IsNoSignature(hresult))
        {
            return Unsigned;
        }

        if (IsUntrusted(hresult))
        {
            return Untrusted;
        }

        return signaturePresent ? Invalid : Unavailable;
    }

    public static bool IsNoSignature(int hresult) =>
        hresult is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown;

    public static string ToStatus(string outcome) => outcome switch
    {
        Valid => "Signed",
        Unsigned => "Unsigned",
        Invalid => "Invalid",
        Untrusted => "Untrusted",
        _ => "Unavailable"
    };

    public static string? Message(string outcome) => outcome switch
    {
        Unsigned => "No embedded or catalog Authenticode signature.",
        Invalid => "The signature is invalid.",
        Untrusted => "The signature is present but the signer is not trusted.",
        Unavailable => "Signature verification could not be completed.",
        _ => null
    };

    private static bool IsRevocationUnavailable(int hresult) =>
        hresult is CryptERevocationOffline or CryptENoRevocationCheck or CertERevocationFailure;

    private static bool IsUntrusted(int hresult) =>
        hresult is CertEUntrustedRoot or CertEChaining or CertEUntrustedTestRoot or TrustEExplicitDistrust or TrustESubjectNotTrusted;
}
