using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vestigo.Security;

/// <summary>
///     Shared ACL and reparse-point evaluation. A file or directory that a lower-privileged principal can modify,
///     replace, or re-ACL is untrusted for anything the service reads or executes with its own identity.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AclTrust
{
    private static readonly HashSet<string> TrustedSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // SYSTEM
        "S-1-5-32-544", // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    private const string CreatorOwnerSid = "S-1-3-0";

    public const FileSystemRights Control =
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete;

    public static bool IsTrusted(string sid, string? extraTrustedSid = null) =>
        TrustedSids.Contains(sid) || (extraTrustedSid is not null && string.Equals(sid, extraTrustedSid, StringComparison.OrdinalIgnoreCase));

    public static bool IsOwnerTrusted(FileSystemSecurity security, string? extraTrustedSid = null) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && IsTrusted(owner.Value, extraTrustedSid);

    /// <summary>Returns the SID of the first untrusted principal that is allowed a dangerous right, or null.</summary>
    public static string? FindUntrustedWriter(FileSystemSecurity security, FileSystemRights dangerous, string? extraTrustedSid = null)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || (rule.FileSystemRights & dangerous) == 0
                || rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }

            if (!IsTrusted(sid.Value, extraTrustedSid) && sid.Value != CreatorOwnerSid)
            {
                return sid.Value;
            }
        }

        return null;
    }

    /// <summary>Returns null when trusted, otherwise a reason phrase.</summary>
    public static string? Untrusted(FileSystemSecurity security, FileSystemRights dangerous, string? extraTrustedSid = null)
    {
        if (!IsOwnerTrusted(security, extraTrustedSid))
        {
            return "is owned by an untrusted principal";
        }

        return FindUntrustedWriter(security, dangerous, extraTrustedSid) is { } sid ? $"is writable by {sid}" : null;
    }
}
