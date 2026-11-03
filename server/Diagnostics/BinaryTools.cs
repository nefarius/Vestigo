using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Security;
using Vestigo.Security.Signatures;

namespace Vestigo.Diagnostics;

/// <summary>PE inspection and signature verification inside configured roots.</summary>
[McpServerToolType]
public sealed class BinaryTools(RootPolicy roots, IPeInspector inspector, ISignatureVerifier signatures, VestigoOptions options)
{
    [McpServerTool(Name = "inspect_binary")]
    [Description("Reads one PE file inside a root with Read access: architecture (x86, x64, arm64, armnt, arm64ec, arm64x), PE32 or PE32+, subsystem, selected DLL characteristics, timestamp, managed flag, CodeView PDB file name, version, size, and SHA-256. Non-PE input fails with not_pe; a broken PE fails with malformed_pe. Fields come from one open of the file. This does not verify a signature.")]
    public OperationResult<BinaryInspection> InspectBinary(
        [Description("File as root:relative\\path or an absolute path inside a root with Read access.")] string path,
        [Description("Compute SHA-256. Defaults to true.")] bool computeSha256 = true)
    {
        PathCheck check = ResolveFile(path);
        if (!check.Success)
        {
            return OperationResult<BinaryInspection>.Fail(check.ErrorCode!, check.Error!);
        }

        return inspector.Inspect(check.FullPath!, computeSha256, options.Diagnostics.MaxInspectBytes);
    }

    [McpServerTool(Name = "verify_file_signature")]
    [Description("Checks the embedded Authenticode signature and, when the file has none, the Windows catalog signature. outcome is unsigned, valid, invalid, untrusted, or unavailable. source is embedded, catalog, or none. unavailable is not the same as unsigned. revocation is none, cacheOnly (default, no network), or online. online requires Vestigo:Diagnostics:AllowOnlineRevocation and may contact revocation servers. Signer fields are omitted unless includeSigner is true. A valid signature does not mean Windows will load the file as a kernel driver.")]
    public OperationResult<SignatureVerification> VerifyFileSignature(
        [Description("File as root:relative\\path or an absolute path inside a root with Read access.")] string path,
        [Description("none, cacheOnly (default), or online.")] string revocation = "cacheOnly",
        [Description("Include signer subject, issuer, thumbprint, and timestamp.")] bool includeSigner = false)
    {
        if (!RevocationModes.TryParse(revocation, out RevocationMode mode))
        {
            return OperationResult<SignatureVerification>.Fail("invalid_request", "revocation must be none, cacheOnly, or online.");
        }

        if (mode == RevocationMode.Online && !options.Diagnostics.AllowOnlineRevocation)
        {
            return OperationResult<SignatureVerification>.Fail(
                "revocation_not_allowed",
                "Online revocation checks require Vestigo:Diagnostics:AllowOnlineRevocation.");
        }

        PathCheck check = ResolveFile(path);
        if (!check.Success)
        {
            return OperationResult<SignatureVerification>.Fail(check.ErrorCode!, check.Error!);
        }

        return OperationResult<SignatureVerification>.Ok(signatures.Verify(check.FullPath!, mode, includeSigner));
    }

    private PathCheck ResolveFile(string path)
    {
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success)
        {
            return check;
        }

        check = FileAccessGuard.Validate(check);
        if (!check.Success)
        {
            return check;
        }

        return Directory.Exists(check.FullPath)
            ? PathCheck.Fail("not_a_file", "Path is a directory.")
            : check;
    }
}
