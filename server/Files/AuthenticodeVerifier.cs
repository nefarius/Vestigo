using System.Security.Cryptography.X509Certificates;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.WinTrust;

namespace Vestigo.Files;

/// <summary>Embedded Authenticode check result. Status is Signed, Unsigned or Invalid.</summary>
public sealed record SignatureInfo(string Status, string? HResult, string? Subject, string? Message);

/// <summary>
///     Checks the embedded Authenticode signature with <c>WinVerifyTrust</c> (no UI, no revocation lookup).
///     Catalog-signed files, such as many Windows components, report <c>Unsigned</c> because they carry no
///     embedded signature.
/// </summary>
public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    public static unsafe SignatureInfo Verify(string path)
    {
        int result;
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
                dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            data.Anonymous.pFile = &fileInfo;
            Guid action = GenericVerifyV2;
            HWND noWindow = new(-1);
            try
            {
                result = PInvoke.WinVerifyTrust(noWindow, &action, &data);
            }
            finally
            {
                data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
                PInvoke.WinVerifyTrust(noWindow, &action, &data);
            }
        }

        if (result == 0)
        {
            return new SignatureInfo("Signed", null, Subject(path), null);
        }

        string hresult = "0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        if (result is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown)
        {
            return new SignatureInfo("Unsigned", hresult, null, "No embedded Authenticode signature.");
        }

        return new SignatureInfo(
            "Invalid",
            hresult,
            Subject(path),
            System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(result));
    }

    private static string? Subject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No loader exists for signed PE files.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
