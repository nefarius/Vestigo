using System.Security.Cryptography.X509Certificates;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography.Catalog;
using Windows.Win32.Security.WinTrust;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Security.Signatures;

/// <summary>
///     <c>WinVerifyTrust</c> for an embedded signature, then a catalog lookup
///     (<c>CryptCATAdminAcquireContext2</c>, <c>CryptCATAdminCalcHashFromFileHandle2</c>,
///     <c>CryptCATAdminEnumCatalogFromHash</c>) verified with <c>WTD_CHOICE_CATALOG</c>.
///     <see cref="RevocationMode.None" /> and <see cref="RevocationMode.CacheOnly" /> set
///     <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> so they do not contact the network.
///     A valid result does not mean Windows will load the file as a kernel driver.
/// </summary>
public sealed class WinTrustSignatureVerifier : ISignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static readonly Guid DriverActionVerify = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    public SignatureVerification Verify(string path, RevocationMode revocation, bool includeSigner)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        try
        {
            return VerifyCore(path, revocation, includeSigner, observedAt);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new SignatureVerification(
                SignatureOutcomeMap.Unavailable,
                SignatureOutcomeMap.SourceNone,
                null,
                RevocationModes.Format(revocation),
                observedAt,
                null);
        }
    }

    private static SignatureVerification VerifyCore(string path, RevocationMode revocation, bool includeSigner, DateTimeOffset observedAt)
    {
        int embedded = VerifyEmbedded(path, revocation);
        CatalogLookup? catalog = null;
        string? catalogPath = null;
        if (embedded != 0 && SignatureOutcomeMap.IsNoSignature(embedded))
        {
            (catalog, catalogPath) = FindCatalog(path, revocation);
        }

        SignatureDecisionResult decision = SignatureDecision.Decide(embedded, catalog);
        SignerDetails? signer = null;
        if (includeSigner && decision.Source != SignatureOutcomeMap.SourceNone)
        {
            string signedFile = decision.Source == SignatureOutcomeMap.SourceCatalog && catalogPath is not null
                ? catalogPath
                : path;
            signer = TryReadSigner(signedFile);
        }

        return new SignatureVerification(
            decision.Outcome,
            decision.Source,
            decision.HResult == 0 ? null : "0x" + decision.HResult.ToString("X8"),
            RevocationModes.Format(revocation),
            observedAt,
            signer);
    }

    private static unsafe int VerifyEmbedded(string path, RevocationMode revocation)
    {
        fixed (char* file = path)
        {
            WINTRUST_FILE_INFO fileInfo = new()
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = file
            };
            return VerifyTrust(revocation, WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE, &fileInfo, catalog: null);
        }
    }

    private static (CatalogLookup Lookup, string? CatalogPath) FindCatalog(string path, RevocationMode revocation)
    {
        foreach (string algorithm in new[] { "SHA256", "SHA1" })
        {
            CatalogSearch generic = SearchCatalog(path, GenericVerifyV2, algorithm, revocation);
            if (generic.Lookup.Kind == CatalogLookupKind.Found)
            {
                return (generic.Lookup, generic.CatalogPath);
            }

            CatalogSearch driver = SearchCatalog(path, DriverActionVerify, algorithm, revocation);
            if (driver.Lookup.Kind == CatalogLookupKind.Found)
            {
                return (driver.Lookup, driver.CatalogPath);
            }

            if (generic.Lookup.Kind == CatalogLookupKind.NotFound && driver.Lookup.Kind == CatalogLookupKind.NotFound)
            {
                continue;
            }

            CatalogLookup failure = driver.Lookup.Kind == CatalogLookupKind.Unavailable ? driver.Lookup : generic.Lookup;
            return (failure, null);
        }

        return (new CatalogLookup(CatalogLookupKind.NotFound, 0), null);
    }

    private static unsafe CatalogSearch SearchCatalog(string path, Guid subsystem, string algorithm, RevocationMode revocation)
    {
        if (!PInvoke.CryptCATAdminAcquireContext2(out nint admin, subsystem, algorithm, null))
        {
            return CatalogSearch.Failed(HresultFromWin32(MarshalGetLastError()));
        }

        try
        {
            using SafeFileHandle file = PInvoke.CreateFile(
                path,
                (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                lpSecurityAttributes: null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                hTemplateFile: null);
            if (file.IsInvalid)
            {
                return CatalogSearch.Failed(HresultFromWin32(MarshalGetLastError()), contextOpened: true);
            }

            uint hashSize = 0;
            if (!PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref hashSize, []) && hashSize == 0)
            {
                return CatalogSearch.Failed(HresultFromWin32(MarshalGetLastError()), contextOpened: true);
            }

            if (hashSize is 0 or > 128)
            {
                return CatalogSearch.Failed(SignatureOutcomeMap.TrustEProviderUnknown, contextOpened: true);
            }

            Span<byte> hash = stackalloc byte[(int)hashSize];
            if (!PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref hashSize, hash))
            {
                return CatalogSearch.Failed(HresultFromWin32(MarshalGetLastError()), contextOpened: true);
            }

            nint cursor = 0;
            CatalogSearch? failedVerification = null;
            int? unread = null;
            try
            {
                for (int count = 0; count < 32; count++)
                {
                    nint previous = cursor;
                    nint info = PInvoke.CryptCATAdminEnumCatalogFromHash(admin, hash, ref cursor);
                    if (info == 0)
                    {
                        break;
                    }

                    if (previous != 0)
                    {
                        PInvoke.CryptCATAdminReleaseCatalogContext(admin, previous, 0);
                    }

                    cursor = info;
                    CATALOG_INFO catalogInfo = new() { cbStruct = (uint)sizeof(CATALOG_INFO) };
                    if (!PInvoke.CryptCATCatalogInfoFromContext(info, ref catalogInfo, 0))
                    {
                        unread ??= HresultFromWin32(MarshalGetLastError());
                        continue;
                    }

                    string catalogPath = catalogInfo.wszCatalogFile.ToString().TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(catalogPath))
                    {
                        unread ??= SignatureOutcomeMap.TrustEProviderUnknown;
                        continue;
                    }

                    int status = VerifyCatalog(path, catalogPath, admin, hash, revocation);
                    if (status == 0)
                    {
                        return new CatalogSearch(new CatalogLookup(CatalogLookupKind.Found, 0), catalogPath, true);
                    }

                    failedVerification ??= new CatalogSearch(new CatalogLookup(CatalogLookupKind.Found, status), catalogPath, true);
                }
            }
            finally
            {
                if (cursor != 0)
                {
                    PInvoke.CryptCATAdminReleaseCatalogContext(admin, cursor, 0);
                }
            }

            if (failedVerification is { } failed)
            {
                return failed;
            }

            return unread is { } hresult
                ? CatalogSearch.Failed(hresult, contextOpened: true)
                : new CatalogSearch(new CatalogLookup(CatalogLookupKind.NotFound, 0), null, true);
        }
        finally
        {
            PInvoke.CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static unsafe int VerifyCatalog(
        string filePath,
        string catalogPath,
        nint admin,
        ReadOnlySpan<byte> hash,
        RevocationMode revocation)
    {
        Span<char> tag = stackalloc char[hash.Length * 2 + 1];
        for (int index = 0; index < hash.Length; index++)
        {
            hash[index].ToString("X2").AsSpan().CopyTo(tag[(index * 2)..]);
        }

        tag[hash.Length * 2] = '\0';
        fixed (char* file = filePath)
        fixed (char* catalog = catalogPath)
        fixed (char* memberTag = tag)
        fixed (byte* hashBytes = hash)
        {
            WINTRUST_CATALOG_INFO catalogInfo = new()
            {
                cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                pcwszCatalogFilePath = catalog,
                pcwszMemberFilePath = file,
                pcwszMemberTag = memberTag,
                pbCalculatedFileHash = hashBytes,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin
            };
            return VerifyTrust(revocation, WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_CATALOG, file: null, &catalogInfo);
        }
    }

    private static unsafe int VerifyTrust(
        RevocationMode revocation,
        WINTRUST_DATA_UNION_CHOICE choice,
        WINTRUST_FILE_INFO* file,
        WINTRUST_CATALOG_INFO* catalog)
    {
        WINTRUST_DATA data = new()
        {
            cbStruct = (uint)sizeof(WINTRUST_DATA),
            dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
            fdwRevocationChecks = revocation == RevocationMode.None
                ? WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE
                : WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_WHOLECHAIN,
            dwUnionChoice = choice,
            dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
            dwProvFlags = Flags(revocation)
        };
        if (file is not null)
        {
            data.Anonymous.pFile = file;
        }

        if (catalog is not null)
        {
            data.Anonymous.pCatalog = catalog;
        }
        Guid action = GenericVerifyV2;
        HWND noWindow = new(-1);
        int result;
        try
        {
            result = PInvoke.WinVerifyTrust(noWindow, &action, &data);
        }
        finally
        {
            data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            PInvoke.WinVerifyTrust(noWindow, &action, &data);
        }

        return result;
    }

    private static WINTRUST_DATA_PROVIDER_FLAGS Flags(RevocationMode revocation) => revocation switch
    {
        RevocationMode.None => WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL | WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE,
        RevocationMode.Online => WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_CHAIN,
        _ => WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL | WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_CHAIN
    };

    private static SignerDetails? TryReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the Authenticode signer certificate.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using X509Certificate2 parsed = new(certificate);
            return new SignerDetails(parsed.Subject, parsed.Issuer, parsed.Thumbprint, null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static int MarshalGetLastError() => System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

    private static int HresultFromWin32(int error) =>
        error <= 0 ? error : unchecked((int)(0x80070000u | (uint)error));

    private readonly record struct CatalogSearch(CatalogLookup Lookup, string? CatalogPath, bool ContextOpened)
    {
        public static CatalogSearch Failed(int hresult, bool contextOpened = false) =>
            new(new CatalogLookup(CatalogLookupKind.Unavailable, hresult), null, contextOpened);
    }
}
