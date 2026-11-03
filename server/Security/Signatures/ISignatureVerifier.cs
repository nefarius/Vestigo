namespace Vestigo.Security.Signatures;

/// <summary>Checks an embedded Authenticode signature, then a Windows catalog signature when the file has none.</summary>
public interface ISignatureVerifier
{
    /// <summary>
    ///     Verifies <paramref name="path" />. Signer fields are read only when <paramref name="includeSigner" /> is true.
    ///     <see cref="RevocationMode.None" /> and <see cref="RevocationMode.CacheOnly" /> do not use the network.
    /// </summary>
    SignatureVerification Verify(string path, RevocationMode revocation, bool includeSigner);
}
