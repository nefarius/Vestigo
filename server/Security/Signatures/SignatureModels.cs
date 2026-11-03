namespace Vestigo.Security.Signatures;

/// <summary>How much revocation checking <see cref="ISignatureVerifier" /> may do.</summary>
public enum RevocationMode
{
    /// <summary>Do not check revocation. No network.</summary>
    None,

    /// <summary>Check revocation only against data already cached on the machine. No network.</summary>
    CacheOnly,

    /// <summary>Check revocation, which may contact revocation servers.</summary>
    Online
}

/// <summary>Signer fields returned only when the caller asked for them.</summary>
public sealed record SignerDetails(string? Subject, string? Issuer, string? Thumbprint, DateTimeOffset? Timestamp);

/// <summary>
///     One signature observation. <see cref="Outcome" /> is <c>unsigned</c>, <c>valid</c>, <c>invalid</c>,
///     <c>untrusted</c>, or <c>unavailable</c>. <see cref="Source" /> is <c>embedded</c>, <c>catalog</c>, or <c>none</c>.
///     <see cref="Timestamp" /> on <see cref="Signer" /> is null when the countersignature was not read.
/// </summary>
public sealed record SignatureVerification(
    string Outcome,
    string Source,
    string? HResult,
    string RevocationMode,
    DateTimeOffset ObservedAt,
    SignerDetails? Signer);

/// <summary>Parses the <c>revocation</c> tool argument.</summary>
public static class RevocationModes
{
    public static bool TryParse(string? text, out RevocationMode mode)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("cacheOnly", StringComparison.OrdinalIgnoreCase))
        {
            mode = RevocationMode.CacheOnly;
            return true;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            mode = RevocationMode.None;
            return true;
        }

        if (text.Equals("online", StringComparison.OrdinalIgnoreCase))
        {
            mode = RevocationMode.Online;
            return true;
        }

        mode = RevocationMode.CacheOnly;
        return false;
    }

    public static string Format(RevocationMode mode) => mode switch
    {
        RevocationMode.None => "none",
        RevocationMode.Online => "online",
        _ => "cacheOnly"
    };
}
