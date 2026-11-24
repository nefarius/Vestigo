#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>What <see cref="HelperDirectoryProvisioning.Prepare" /> found at <c>spool\helper</c>.</summary>
    public enum HelperDirectoryState
    {
        /// <summary>The directory does not exist (or a stale one was removed) and must be created after <c>spool</c> is secured.</summary>
        NeedsProvision,

        /// <summary>The directory already has a trusted owner and a protected SYSTEM and Administrators DACL.</summary>
        AlreadySecure
    }

    /// <summary>Owner and DACL facts for one directory, already reduced to what the trust decision needs.</summary>
    public sealed class HelperDirectorySecurityState
    {
        public HelperDirectorySecurityState(
            string? ownerSid,
            bool accessRulesProtected,
            IReadOnlyList<string> untrustedAllowSids,
            bool systemHasInheritedFullControl,
            bool administratorsHaveInheritedFullControl)
        {
            OwnerSid = ownerSid;
            AccessRulesProtected = accessRulesProtected;
            UntrustedAllowSids = untrustedAllowSids;
            SystemHasInheritedFullControl = systemHasInheritedFullControl;
            AdministratorsHaveInheritedFullControl = administratorsHaveInheritedFullControl;
        }

        public string? OwnerSid { get; }

        public bool AccessRulesProtected { get; }

        /// <summary>SIDs other than SYSTEM and Administrators that hold an Allow entry.</summary>
        public IReadOnlyList<string> UntrustedAllowSids { get; }

        /// <summary>SYSTEM has FullControl that flows to child containers and objects.</summary>
        public bool SystemHasInheritedFullControl { get; }

        /// <summary>Administrators have FullControl that flows to child containers and objects.</summary>
        public bool AdministratorsHaveInheritedFullControl { get; }

        /// <summary>Returns null when the directory meets the state-directory policy, otherwise the reason it does not.</summary>
        public string? Untrusted()
        {
            List<string> reasons = new List<string>();
            if (!HelperDirectoryProvisioning.IsTrustedOwner(OwnerSid))
            {
                reasons.Add("it is owned by an untrusted principal (" + (OwnerSid ?? "unknown") + ")");
            }

            if (!AccessRulesProtected)
            {
                reasons.Add("its ACL is not protected (inheritance is enabled)");
            }

            if (UntrustedAllowSids.Count > 0)
            {
                reasons.Add("its ACL allows " + string.Join(", ", UntrustedAllowSids));
            }

            if (!SystemHasInheritedFullControl || !AdministratorsHaveInheritedFullControl)
            {
                reasons.Add("SYSTEM and Administrators do not both have inheritable full control");
            }

            return reasons.Count == 0 ? null : string.Join("; ", reasons);
        }
    }

    /// <summary>One immediate child of the helper directory, described without following links.</summary>
    public sealed class HelperDirectoryEntry
    {
        public HelperDirectoryEntry(string path, bool isDirectory, bool isReparsePoint)
        {
            Path = path;
            IsDirectory = isDirectory;
            IsReparsePoint = isReparsePoint;
        }

        public string Path { get; }

        public bool IsDirectory { get; }

        public bool IsReparsePoint { get; }
    }

    /// <summary>File-system operations for helper provisioning. Tests supply fakes.</summary>
    public interface IHelperDirectoryStore
    {
        bool FileExists(string path);

        bool DirectoryExists(string path);

        /// <summary>True for a file or directory that is a reparse point. Never follows the link.</summary>
        bool IsReparsePoint(string path);

        HelperDirectorySecurityState ReadSecurity(string directory);

        /// <summary>Immediate children only, without following links.</summary>
        IReadOnlyList<HelperDirectoryEntry> ListEntries(string directory);

        void DeleteFile(string path);

        /// <summary>Removes an empty directory. Must not recurse.</summary>
        void DeleteEmptyDirectory(string path);

        /// <summary>Creates the directory and applies the Administrators owner and protected SYSTEM and Administrators DACL.</summary>
        void CreateSecured(string path);

        void Log(string message);
    }

    /// <summary>
    ///     Provisions <c>spool\helper</c>, which the service creates lazily for interactive helper output. The
    ///     directory is checked at runtime (trusted owner, no link), so a stale one left by an older install must be
    ///     dealt with before the service starts. Only Vestigo's own per-run scratch files are treated as disposable;
    ///     anything else is left alone and the install fails with the exact path and reason.
    /// </summary>
    public static class HelperDirectoryProvisioning
    {
        public const string DirectoryName = "helper";

        private const string SystemSid = "S-1-5-18";
        private const string AdministratorsSid = "S-1-5-32-544";

        public static string HelperPath(string spoolDirectory)
        {
            return System.IO.Path.Combine(spoolDirectory, DirectoryName);
        }

        public static bool IsTrustedOwner(string? sid)
        {
            return string.Equals(sid, SystemSid, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Names the service writes into the helper directory: a 32-digit hex id plus <c>.json</c> or <c>.events</c>,
        ///     or <c>desktop-host-</c> plus that id plus <c>.log</c>. All are per-run scratch output.
        /// </summary>
        public static bool IsDisposableScratchName(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            string name = fileName!;
            string? stem = null;
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".events", StringComparison.OrdinalIgnoreCase))
            {
                stem = name.Substring(0, name.LastIndexOf('.'));
            }
            else if (name.StartsWith("desktop-host-", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                     name.Length > "desktop-host-".Length + ".log".Length)
            {
                stem = name.Substring("desktop-host-".Length, name.Length - "desktop-host-".Length - ".log".Length);
            }

            return stem != null && stem.Length == 32 && stem.All(IsHexDigit);
        }

        /// <summary>
        ///     Runs before the parent <c>spool</c> ACL is rewritten, so a stale directory never receives inherited trust.
        ///     Throws <see cref="InvalidOperationException" /> naming the path and reason when it cannot be handled safely.
        /// </summary>
        public static HelperDirectoryState Prepare(string spoolDirectory, IHelperDirectoryStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            string helper = HelperPath(spoolDirectory);
            RejectLinkOrFile(store, helper);
            if (!store.DirectoryExists(helper))
            {
                return HelperDirectoryState.NeedsProvision;
            }

            string? reason = store.ReadSecurity(helper).Untrusted();
            if (reason == null)
            {
                store.Log("'" + helper + "' already has a trusted owner and a protected SYSTEM and Administrators ACL.");
                return HelperDirectoryState.AlreadySecure;
            }

            // Look at everything before deleting anything, so a refusal leaves existing state untouched.
            IReadOnlyList<HelperDirectoryEntry> entries = store.ListEntries(helper);
            foreach (HelperDirectoryEntry entry in entries)
            {
                if (entry.IsReparsePoint)
                {
                    throw Refuse(helper, reason, "'" + entry.Path + "' is a reparse point");
                }

                if (entry.IsDirectory)
                {
                    throw Refuse(helper, reason, "'" + entry.Path + "' is a directory");
                }

                if (!IsDisposableScratchName(System.IO.Path.GetFileName(entry.Path)))
                {
                    throw Refuse(helper, reason, "'" + entry.Path + "' is not a Vestigo helper scratch file");
                }
            }

            // The directory is writable by an untrusted principal here, so re-check the chain right before each
            // delete: a link swapped in after the listing must not redirect File.Delete elsewhere.
            foreach (HelperDirectoryEntry entry in entries)
            {
                RequireUnredirected(store, spoolDirectory, helper, entry.Path);
                store.DeleteFile(entry.Path);
            }

            RequireUnredirected(store, spoolDirectory, helper, null);
            store.DeleteEmptyDirectory(helper);
            store.Log("Removed stale helper directory '" + helper + "' because " + reason + "; deleted " +
                      entries.Count.ToString(CultureInfo.InvariantCulture) + " scratch file(s). It is recreated with a protected ACL.");
            return HelperDirectoryState.NeedsProvision;
        }

        /// <summary>
        ///     Runs after <c>spool</c> is secured. A helper judged secure earlier is checked again against the live
        ///     directory, because a principal that could write the old <c>spool</c> may have replaced it meanwhile.
        ///     An unchanged trusted helper is left in place.
        /// </summary>
        public static void Complete(string spoolDirectory, IHelperDirectoryStore store, HelperDirectoryState state)
        {
            if (state == HelperDirectoryState.AlreadySecure)
            {
                state = Prepare(spoolDirectory, store);
            }

            if (state == HelperDirectoryState.NeedsProvision)
            {
                Provision(spoolDirectory, store);
            }
        }

        /// <summary>Creates the directory and verifies the result.</summary>
        public static void Provision(string spoolDirectory, IHelperDirectoryStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            string helper = HelperPath(spoolDirectory);
            RejectLinkOrFile(store, helper);
            if (store.DirectoryExists(helper))
            {
                // Something appeared after Prepare; never adopt it silently.
                throw new InvalidOperationException(
                    "Cannot provision '" + helper + "': the directory appeared after it was checked. Remove it and repair the installation.");
            }

            store.CreateSecured(helper);
            RejectLinkOrFile(store, helper);
            string? reason = store.ReadSecurity(helper).Untrusted();
            if (reason != null)
            {
                throw new InvalidOperationException(
                    "Created '" + helper + "' but it does not meet the state-directory policy because " + reason + ".");
            }

            store.Log("Provisioned '" + helper + "' with a protected SYSTEM and Administrators ACL.");
        }

        private static void RequireUnredirected(IHelperDirectoryStore store, string spoolDirectory, string helper, string? entry)
        {
            string? moved = null;
            if (store.IsReparsePoint(spoolDirectory))
            {
                moved = spoolDirectory;
            }
            else if (!store.DirectoryExists(helper) || store.IsReparsePoint(helper))
            {
                moved = helper;
            }
            else if (entry != null && (store.DirectoryExists(entry) || store.IsReparsePoint(entry)))
            {
                moved = entry;
            }

            if (moved != null)
            {
                throw new InvalidOperationException(
                    "'" + moved + "' changed while '" + helper + "' was being cleaned up (it is now a link, directory or missing); " +
                    "nothing further was deleted. Review it and repair the installation.");
            }
        }

        private static void RejectLinkOrFile(IHelperDirectoryStore store, string helper)
        {
            if ((store.FileExists(helper) || store.DirectoryExists(helper)) && store.IsReparsePoint(helper))
            {
                throw new InvalidOperationException(
                    "'" + helper + "' is a reparse point; refusing to use it for Vestigo state. Remove the link and repair the installation.");
            }

            if (store.FileExists(helper))
            {
                throw new InvalidOperationException(
                    "'" + helper + "' is a file, not a directory; refusing to replace it. Remove it and repair the installation.");
            }
        }

        private static InvalidOperationException Refuse(string helper, string reason, string offender)
        {
            return new InvalidOperationException(
                "'" + helper + "' is not trusted because " + reason + ", and it holds data Vestigo will not delete: " + offender +
                ". Move or remove the directory contents after reviewing them, then repair the installation.");
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
