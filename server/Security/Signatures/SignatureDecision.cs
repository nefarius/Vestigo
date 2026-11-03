namespace Vestigo.Security.Signatures;

internal enum CatalogLookupKind
{
    NotFound,
    Found,
    Unavailable
}

/// <summary>Catalog lookup outcome. The catalog path never leaves the verifier.</summary>
internal readonly record struct CatalogLookup(CatalogLookupKind Kind, int HResult);

internal readonly record struct SignatureDecisionResult(string Outcome, string Source, int HResult);

/// <summary>
///     Combines the embedded WinVerifyTrust result with a catalog lookup. A catalog infrastructure failure stays
///     <c>unavailable</c> and is never reported as unsigned.
/// </summary>
internal static class SignatureDecision
{
    public static SignatureDecisionResult Decide(int embedded, CatalogLookup? catalog)
    {
        if (embedded == 0)
        {
            return new SignatureDecisionResult(SignatureOutcomeMap.Valid, SignatureOutcomeMap.SourceEmbedded, 0);
        }

        if (!SignatureOutcomeMap.IsNoSignature(embedded))
        {
            return new SignatureDecisionResult(
                SignatureOutcomeMap.Map(embedded, signaturePresent: true),
                SignatureOutcomeMap.SourceEmbedded,
                embedded);
        }

        if (catalog is null || catalog.Value.Kind == CatalogLookupKind.Unavailable)
        {
            int hresult = catalog?.HResult ?? embedded;
            return new SignatureDecisionResult(SignatureOutcomeMap.Unavailable, SignatureOutcomeMap.SourceNone, hresult);
        }

        if (catalog.Value.Kind == CatalogLookupKind.NotFound)
        {
            return new SignatureDecisionResult(SignatureOutcomeMap.Unsigned, SignatureOutcomeMap.SourceNone, embedded);
        }

        return new SignatureDecisionResult(
            SignatureOutcomeMap.Map(catalog.Value.HResult, signaturePresent: true),
            SignatureOutcomeMap.SourceCatalog,
            catalog.Value.HResult);
    }
}
