#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>
    ///     NTFS implementation of <see cref="IHelperDirectoryStore" /> for the elevated MSI action. Compiled into the
    ///     net48 custom action and into the tests, so both exercise the same code. Failures carry the exact path.
    /// </summary>
#if NET
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed class WindowsHelperDirectoryStore : IHelperDirectoryStore
    {
        private readonly Action<string> _log;

        public WindowsHelperDirectoryStore(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return false;
            }
        }

        public HelperDirectorySecurityState ReadSecurity(string directory)
        {
            try
            {
                DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl();
                SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                SecurityIdentifier? owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                List<string> untrusted = new List<string>();
                bool systemFull = false;
                bool administratorsFull = false;
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow)
                    {
                        continue;
                    }

                    SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
                    bool full = (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
                                rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
                    if (sid.Equals(system))
                    {
                        systemFull |= full;
                    }
                    else if (sid.Equals(administrators))
                    {
                        administratorsFull |= full;
                    }
                    else if (!untrusted.Contains(sid.Value))
                    {
                        untrusted.Add(sid.Value);
                    }
                }

                return new HelperDirectorySecurityState(
                    owner == null ? null : owner.Value,
                    security.AreAccessRulesProtected,
                    untrusted,
                    systemFull,
                    administratorsFull);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException("Could not read the permissions of '" + directory + "': " + ex.Message, ex);
            }
        }

        public IReadOnlyList<HelperDirectoryEntry> ListEntries(string directory)
        {
            try
            {
                List<HelperDirectoryEntry> entries = new List<HelperDirectoryEntry>();
                foreach (FileSystemInfo info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    FileAttributes attributes = info.Attributes;
                    entries.Add(new HelperDirectoryEntry(
                        info.FullName,
                        (attributes & FileAttributes.Directory) != 0,
                        (attributes & FileAttributes.ReparsePoint) != 0));
                }

                return entries;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Could not list '" + directory + "': " + ex.Message, ex);
            }
        }

        public void DeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Could not delete stale helper file '" + path + "': " + ex.Message, ex);
            }
        }

        public void DeleteEmptyDirectory(string path)
        {
            try
            {
                Directory.Delete(path, false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Could not remove stale helper directory '" + path + "': " + ex.Message, ex);
            }
        }

        public void CreateSecured(string path)
        {
            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            try
            {
                DirectoryInfo info = new DirectoryInfo(path);
                info.Create(ProtectedSecurity(system, administrators));

                // The creator's default owner wins over the descriptor's, so set owner and DACL on the new, empty directory.
                info.SetAccessControl(ProtectedSecurity(system, administrators));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException("Could not create '" + path + "' with a protected ACL: " + ex.Message, ex);
            }
        }

        public void Log(string message)
        {
            _log(message);
        }

        /// <summary>The policy applied to every Vestigo state directory: Administrators own it; SYSTEM and Administrators only, inheritance off.</summary>
        public static DirectorySecurity ProtectedSecurity(SecurityIdentifier system, SecurityIdentifier administrators)
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }
    }
}
