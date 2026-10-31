using System.Security.Cryptography.X509Certificates;

using Windows.Win32;
using Windows.Win32.Security.WinTrust;

namespace Vestigo.RemoteDebugger;

/// <summary>Verifies the Authenticode signature of a downloaded file.</summary>
public interface IAuthenticodeVerifier
{
    /// <summary>Returns the signer subject when the signature chains to a trusted root, else an error.</summary>
    OperationResult<string> Verify(string path);
}

/// <summary>
///     <c>WinVerifyTrust</c> (WINTRUST_ACTION_GENERIC_VERIFY_V2, whole chain, no UI) for validity, then the signer
///     certificate subject from the file.
/// </summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public unsafe OperationResult<string> Verify(string path)
    {
        fixed (char* file = path)
        {
            WINTRUST_FILE_INFO fileInfo = new()
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = file
            };
            WINTRUST_DATA data = new()
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
                fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
                dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
                dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
                dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_CHAIN
            };
            data.Anonymous.pFile = &fileInfo;
            Guid action = GenericVerifyV2;
            int status = PInvoke.WinVerifyTrust(default, ref action, &data);
            data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            PInvoke.WinVerifyTrust(default, ref action, &data);
            if (status != 0)
            {
                return OperationResult<string>.Fail(
                    "signature_invalid",
                    $"WinVerifyTrust rejected the installer (0x{status:X8}); it is unsigned, tampered with, or untrusted.");
            }
        }

        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads the Authenticode signer; validity was checked above.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return OperationResult<string>.Ok(certificate.Subject);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return OperationResult<string>.Fail("signature_invalid", "Could not read the signer certificate: " + ex.Message);
        }
    }
}