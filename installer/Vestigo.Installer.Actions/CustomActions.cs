#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using Microsoft.Win32;

using Vestigo.Installer.Actions.Logic;

using WixToolset.Dtf.WindowsInstaller;

namespace Vestigo.Installer.Actions
{
    /// <summary>
    ///     MSI custom actions for the access and authentication pages. Token values are never written to the MSI log.
    ///     Properties: VESTIGO_LISTEN_MODE, VESTIGO_AUTH_MODE, VESTIGO_PORT, VESTIGO_API_TOKEN (public, hidden token),
    ///     VESTIGO_ROOTS_MODE, VESTIGO_ROOTS_ACCESS,
    ///     VESTIGO_INPUT_ERROR (dialog validation text),
    ///     VESTIGO_TOKEN_SUGGESTED (auto-generated token the user may not have edited),
    ///     VESTIGO_SETTINGS_PROTECTED (settings file exists but is unreadable before elevation),
    ///     VESTIGO_AUTH_UI_MODE (keep / replace / none when settings are protected),
    ///     VESTIGO_VALIDATE_TOKEN (1 when Next must reject an empty replace-token field).
    /// </summary>
    public static class CustomActions
    {
        private const string RootsModeProperty = "VESTIGO_ROOTS_MODE";
        private const string RootsAccessProperty = "VESTIGO_ROOTS_ACCESS";
        private const string RootsAccessPrefillProperty = "VESTIGO_ROOTS_ACCESS_PREFILL";
        private const string RootsAccessExplicitProperty = "VESTIGO_ROOTS_ACCESS_EXPLICIT";
        private const string ErrorProperty = "VESTIGO_INPUT_ERROR";
        private const string WriteDataProperty = "VestigoWriteSettings";
        private const string DeployCredentialProviderProperty = "VestigoDeployCredentialProvider";
        private const string RemoveCredentialProviderProperty = "VestigoRemoveCredentialProvider";
        private const int MoveFileDelayUntilReboot = 4;

        private const string CompanyFolder = "Nefarius Software Solutions";
        private const string ProductFolder = "Vestigo";

        /// <summary>
        ///     Immediate, before LaunchConditions. Sets VESTIGO_X64_ASPNET10 when the 32-bit-view x64
        ///     AspNetCore.App and NETCore.App sharedfx keys both have a 10.0.x value name and the
        ///     highest NETCore patch is at least the highest ASP.NET Core patch. Never fails the install.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoDetectX64AspNet10(Session session)
        {
            try
            {
                string[] aspNetNames = ReadX64SharedFxValueNames(AspNetRuntimePrerequisite.RegistryKey);
                string[] netCoreNames = ReadX64SharedFxValueNames(AspNetRuntimePrerequisite.NetCoreRegistryKey);
                if (AspNetRuntimePrerequisite.HasCompatibleRuntimes(aspNetNames, netCoreNames))
                {
                    session[AspNetRuntimePrerequisite.RegistryProperty] = "1";
                }
                else
                {
                    LogMissingRuntimes(session, aspNetNames, netCoreNames);
                }
            }
            catch (Exception ex)
            {
                session.Log("Could not detect x64 ASP.NET 10: " + ex.GetType().Name + ": " + ex.Message);
            }

            return ActionResult.Success;
        }

        private static void LogMissingRuntimes(Session session, string[] aspNetNames, string[] netCoreNames)
        {
            bool aspNetPresent = AspNetRuntimePrerequisite.HasCompatibleRuntime(aspNetNames);
            bool netCorePresent = AspNetRuntimePrerequisite.HasCompatibleRuntime(netCoreNames);
            if (!aspNetPresent)
            {
                session.Log("x64 Microsoft.AspNetCore.App 10.0.x is not installed.");
            }

            if (!netCorePresent)
            {
                session.Log("x64 Microsoft.NETCore.App 10.0.x is not installed.");
            }

            if (aspNetPresent && netCorePresent)
            {
                session.Log(
                    "x64 Microsoft.NETCore.App 10.0.x is older than Microsoft.AspNetCore.App 10.0.x.");
            }
        }

        private static string[] ReadX64SharedFxValueNames(string key)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (RegistryKey opened = baseKey.OpenSubKey(key))
            {
                return opened == null ? Array.Empty<string>() : opened.GetValueNames();
            }
        }

        /// <summary>
        ///     Immediate, early in both sequences. Fills the VESTIGO_* properties from explicit values, the existing
        ///     settings file, and defaults (any address with a token). A silent install with invalid values fails here.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoLoadSettings(Session session)
        {
            try
            {
                SettingsFileRead file = ReadSettingsFile();
                bool isUpgrade = IsUpgrade(session);
                SessionProperties properties = new SessionProperties(session);
                SettingsHandoff.Load(
                    properties,
                    file.State == SettingsFileState.Protected ? null : file.Json,
                    file.State == SettingsFileState.Protected,
                    isUpgrade,
                    IsInstalled(session),
                    null);

                bool accessExplicit = AccessIsExplicit(session);
                PublishRoots(
                    session,
                    RootSettings.Resolve(
                        session[RootsModeProperty],
                        session[RootsAccessProperty],
                        RootSettings.ReadExisting(file.Json),
                        isUpgrade,
                        accessExplicit),
                    accessExplicit);

                // Carry existing module choices into the feature tree (Level conditions read these properties).
                foreach (KeyValuePair<string, bool> state in ModuleSelection.ReadExisting(file.Json))
                {
                    ModuleDefinition module = ModuleSelection.Find(state.Key);
                    if (module != null && string.IsNullOrWhiteSpace(session[module.PropertyName]))
                    {
                        session[module.PropertyName] = state.Value ? "1" : "0";
                    }
                }

                session.Log(SettingsHandoff.Describe(properties));
                return ActionResult.Success;
            }
            catch (ArgumentException ex)
            {
                session.Log("Invalid Vestigo install settings: " + ex.Message);
                using (Record record = new Record(0))
                {
                    record.FormatString = "Invalid Vestigo settings: " + ex.Message;
                    session.Message(InstallMessage.Error | (InstallMessage)0x10 /* MB_ICONERROR */, record);
                }

                return ActionResult.Failure;
            }
        }

        /// <summary>
        ///     Immediate, called by each dialog's Next button. Validates the listen, auth, port, and token properties.
        ///     Problems are reported through VESTIGO_INPUT_ERROR; it never fails the install.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoApplyUiChoice(Session session)
        {
            session[ErrorProperty] = string.Empty;
            try
            {
                SettingsFileRead file = ReadSettingsFile();
                bool isUpgrade = IsUpgrade(session);
                SessionProperties properties = new SessionProperties(session);
                SettingsHandoff.Apply(
                    properties,
                    file.State == SettingsFileState.Protected ? null : file.Json,
                    file.State == SettingsFileState.Protected,
                    isUpgrade,
                    IsInstalled(session));
                if (!string.IsNullOrEmpty(session[ErrorProperty]))
                {
                    return ActionResult.Success;
                }

                bool accessExplicit = AccessIsExplicit(session);
                PublishRoots(
                    session,
                    RootSettings.Resolve(
                        session[RootsModeProperty],
                        session[RootsAccessProperty],
                        RootSettings.ReadExisting(file.Json),
                        isUpgrade,
                        accessExplicit),
                    accessExplicit);
                session.Log(SettingsHandoff.Describe(properties));
            }
            catch (ArgumentException ex)
            {
                session[ErrorProperty] = ex.Message;
            }

            return ActionResult.Success;
        }

        /// <summary>
        ///     Immediate, in the execute sequence before the deferred write. Packs the chosen values into the deferred
        ///     action's CustomActionData property, which is listed in MsiHiddenProperties.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoPrepareWriteSettings(Session session)
        {
            try
            {
                Dictionary<string, string> values = SettingsHandoff.Prepare(
                    new SessionProperties(session),
                    IsUpgrade(session),
                    IsInstalled(session),
                    ModuleSelection.Serialize(ReadFeatureStates(session)));
                CustomActionData data = new CustomActionData();
                foreach (KeyValuePair<string, string> pair in values)
                {
                    data[pair.Key] = pair.Value ?? string.Empty;
                }

                session[WriteDataProperty] = data.ToString();
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not prepare Vestigo settings: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        /// <summary>
        ///     Deferred, elevated, before StartServices. Merges Vestigo:ListenAddress, ListenPort, ApiTokens,
        ///     VolumeRoots, VolumeRootAccess, Modules, Software:AllowUninstall, ScheduledTasks:AllowChanges, Audio:AllowSetDefault,
        ///     System:AllowBootConfigurationChanges, System:AllowUacChanges, System:AllowReboot,
        ///     System:AllowAutoLogonChanges, System:AllowInteractiveLogon, and Desktop:AllowScreenshots into the ProgramData
        ///     appsettings.json, keeping every other key, as UTF-8 without BOM readable only by SYSTEM and Administrators.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoWriteSettings(Session session)
        {
            try
            {
                // First, so reparse points are rejected before any settings-file read or write.
                SecureStateDirectories(session);
                CustomActionData data = session.CustomActionData;
                string path = SettingsFilePath();
                string current = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                ExistingSettings existing = ListenSettings.ReadExisting(current);
                ResolvedSettings resolved = SettingsHandoff.ResolveWrite(ReadActionData(data), existing);
                IDictionary<string, bool> modules = ModuleSelection.Parse(data["Modules"]);
                ResolvedRootSettings roots = RootSettings.Resolve(
                    data["RootsMode"],
                    data["RootsAccess"],
                    RootSettings.ReadExisting(current),
                    accessIsExplicit: data["RootsAccessExplicit"] == "1");

                string merged;
                try
                {
                    merged = ListenSettings.Merge(current, resolved, modules, roots);
                }
                catch (FormatException ex)
                {
                    string backup = path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    session.Log("Existing settings file is not valid JSON (" + ex.Message + "). Keeping it as " + backup + ".");
                    WriteSecureFile(backup, File.ReadAllBytes(path));
                    merged = ListenSettings.Merge(null, resolved, modules, roots);
                }

                string temp = path + ".tmp";
                WriteSecureFile(temp, new UTF8Encoding(false).GetBytes(merged));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
                SecureFile(path);
                session.Log("Wrote Vestigo settings to " + path + " (listen=" + resolved.ListenMode + ", auth=" + resolved.AuthMode +
                            ", port=" + resolved.Port + ", roots=" + roots.Mode + ", access=" + roots.Access + ").");
                try
                {
                    using (WindowsFirewallRuleStore store = new WindowsFirewallRuleStore())
                    {
                        FirewallRuleSync.Sync(resolved, store, message => session.Log(message));
                    }
                }
                catch (Exception firewallEx)
                {
                    session.Log("Could not update the Windows Firewall rule: " + firewallEx.GetType().Name + ": " + firewallEx.Message);
                }

                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not write Vestigo settings: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        /// <summary>
        ///     Deferred, elevated, before RemoveFiles on a real uninstall. Major upgrade skips this and lets the new product's
        ///     settings write recreate the rule from the preserved listen address and port. Failure is logged and does not
        ///     block uninstall.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoRemoveFirewallRule(Session session)
        {
            try
            {
                using (WindowsFirewallRuleStore store = new WindowsFirewallRuleStore())
                {
                    FirewallRuleSync.Remove(store, message => session.Log(message));
                }
            }
            catch (Exception ex)
            {
                session.Log("Could not remove the Windows Firewall rule: " + ex.GetType().Name + ": " + ex.Message);
            }

            return ActionResult.Success;
        }

        /// <summary>
        ///     Immediate. Packs INSTALLDIR and whether this is a major-upgrade removal for the deferred Credential Provider actions.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoPrepareCredentialProvider(Session session)
        {
            try
            {
                CustomActionData data = new CustomActionData();
                data["InstallDir"] = session["INSTALLDIR"];
                data["Upgrading"] = string.IsNullOrWhiteSpace(session["UPGRADINGPRODUCTCODE"]) ? "0" : "1";
                string packed = data.ToString();
                session[DeployCredentialProviderProperty] = packed;
                session[RemoveCredentialProviderProperty] = packed;
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not prepare Credential Provider action: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        /// <summary>
        ///     Deferred. Copies the staged Credential Provider to a hash-suffixed live path and updates InprocServer32.
        ///     An unchanged hash leaves a LogonUI-mapped DLL untouched so upgrades stay reboot-free.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoDeployCredentialProvider(Session session)
        {
            try
            {
                CustomActionData data = session.CustomActionData;
                string installDir = data["InstallDir"];
                WindowsCredentialProviderStore store = new WindowsCredentialProviderStore();
                CredentialProviderDeployResult result = CredentialProviderDeployment.Deploy(installDir, store);
                session.Log("Credential Provider deploy " + result.Action + " live=" + result.LivePath +
                            " registration=" + result.RegistrationUpdated +
                            " leftover=" + result.LeftoverLocked.Count);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not deploy Credential Provider: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        /// <summary>
        ///     Deferred. Unregisters the Credential Provider and best-effort deletes live DLLs. Skipped during a major upgrade.
        ///     A locked live file is scheduled for deletion at the next reboot without forcing one.
        /// </summary>
        [CustomAction]
        public static ActionResult VestigoRemoveCredentialProvider(Session session)
        {
            try
            {
                CustomActionData data = session.CustomActionData;
                string installDir = data["InstallDir"];
                bool upgrading = data["Upgrading"] == "1";
                WindowsCredentialProviderStore store = new WindowsCredentialProviderStore();
                CredentialProviderDeployResult result = CredentialProviderDeployment.Remove(installDir, upgrading, store);
                foreach (string leftover in result.LeftoverLocked)
                {
                    if (MoveFileEx(leftover, null, MoveFileDelayUntilReboot))
                    {
                        session.Log("Scheduled locked Credential Provider DLL for delete on reboot: " + leftover);
                    }
                    else
                    {
                        session.Log("Could not schedule delete for locked Credential Provider DLL: " + leftover);
                    }
                }

                session.Log("Credential Provider remove " + result.Action + " leftover=" + result.LeftoverLocked.Count);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not remove Credential Provider: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        /// <summary>
        ///     A module is enabled when its feature will be installed locally; when the request state is unknown the
        ///     current state decides (repair and modify keep untouched features as they are). DesktopScreenshots is
        ///     omitted when it is a new, unselected feature so an unelevated upgrade does not write AllowScreenshots=false.
        /// </summary>
        private static IDictionary<string, bool> ReadFeatureStates(Session session)
        {
            Dictionary<string, bool> states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModuleDefinition module in ModuleSelection.All)
            {
                try
                {
                    FeatureInfo feature = session.Features[module.Name];
                    InstallState request = feature.RequestState;
                    bool currentlyInstalled = feature.CurrentState == InstallState.Local;
                    bool requestIsExplicit = request != InstallState.Unknown &&
                                             request != InstallState.Default &&
                                             request != InstallState.NotUsed &&
                                             ModuleSelection.IsExplicitFeatureRequest(
                                                 currentlyInstalled,
                                                 request == InstallState.Absent);
                    if (!ModuleSelection.ShouldWriteFeatureState(module.Name, requestIsExplicit, currentlyInstalled))
                    {
                        continue;
                    }

                    if (!requestIsExplicit)
                    {
                        request = feature.CurrentState;
                    }

                    states[module.Name] = request == InstallState.Local;
                }
                catch (InstallerException ex)
                {
                    session.Log("Could not read feature " + module.Name + ": " + ex.Message);
                }
            }

            return states;
        }

        /// <summary>True when FindRelatedProducts flagged an older version (set before AppSearch in both sequences).</summary>
        private static bool IsUpgrade(Session session)
        {
            return !string.IsNullOrWhiteSpace(session["WIX_UPGRADE_DETECTED"]);
        }

        /// <summary>Set to the product code when this product is already installed (repair and modify).</summary>
        private static bool IsInstalled(Session session)
        {
            return !string.IsNullOrWhiteSpace(session["Installed"]);
        }

        private static Dictionary<string, string> ReadActionData(CustomActionData data)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in SettingsHandoff.WriteDataKeys)
            {
                map[key] = data[key];
            }

            return map;
        }

        private static void PublishRoots(Session session, ResolvedRootSettings roots, bool accessExplicit)
        {
            if (string.IsNullOrWhiteSpace(session[RootsAccessPrefillProperty]))
            {
                session[RootsAccessPrefillProperty] = roots.Access;
            }

            session[RootsModeProperty] = roots.Mode;
            session[RootsAccessProperty] = roots.Access;
            session[RootsAccessExplicitProperty] = accessExplicit ? "1" : "0";
        }

        private static bool AccessIsExplicit(Session session)
        {
            return SettingsHandoff.AccessIsExplicit(new SessionProperties(session));
        }

        private static string SettingsFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                CompanyFolder,
                ProductFolder,
                "appsettings.json");
        }

        /// <summary>
        ///     Distinguishes a missing settings file from one that exists but cannot be read before elevation.
        ///     <see cref="File.Exists" /> is not used: it returns false on <c>ERROR_ACCESS_DENIED</c>.
        /// </summary>
        private static SettingsFileRead ReadSettingsFile()
        {
            string path = SettingsFilePath();
            try
            {
                return new SettingsFileRead(SettingsFileState.Found, File.ReadAllText(path, Encoding.UTF8));
            }
            catch (UnauthorizedAccessException)
            {
                return SettingsFileRead.Protected;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return SettingsFileRead.Missing;
            }
            catch (IOException)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                        ? SettingsFileRead.Protected
                        : SettingsFileRead.Missing;
                }
                catch (UnauthorizedAccessException)
                {
                    return SettingsFileRead.Protected;
                }
            }
        }

        private enum SettingsFileState
        {
            Missing,
            Protected,
            Found
        }

        private sealed class SettingsFileRead
        {
            public static readonly SettingsFileRead Missing = new SettingsFileRead(SettingsFileState.Missing, null);
            public static readonly SettingsFileRead Protected = new SettingsFileRead(SettingsFileState.Protected, null);

            public SettingsFileRead(SettingsFileState state, string json)
            {
                State = state;
                Json = json;
            }

            public SettingsFileState State { get; }

            public string Json { get; }
        }

        /// <summary>Creates the file with the restricted ACL already applied, so the token is never readable by others.</summary>
        private static void WriteSecureFile(string path, byte[] content)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using (FileStream stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileSystemRights.WriteData | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions,
                       FileShare.None,
                       4096,
                       FileOptions.None,
                       RestrictedSecurity()))
            {
                stream.Write(content, 0, content.Length);
            }
        }

        /// <summary>
        ///     Creates (or re-secures on upgrade) the ProgramData state tree (settings, logs, spool, symcache, traces,
        ///     and spool\helper) with a protected SYSTEM + Administrators DACL and fails the install when a component is
        ///     a reparse point. A stale spool\helper with an untrusted owner or ACL is replaced only when it holds nothing
        ///     but Vestigo's per-run scratch files; otherwise the install fails naming the path and reason.
        ///     The company folder's ACL is always reset; an untrusted owner is logged and replaced. Other Nefarius
        ///     products' subfolders are left alone.
        /// </summary>
        private static void SecureStateDirectories(Session session)
        {
            string company = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                CompanyFolder);
            string product = Path.Combine(company, ProductFolder);
            string[] managed =
            {
                product,
                Path.Combine(product, "logs"),
                Path.Combine(product, "spool"),
                Path.Combine(product, "symcache"),
                Path.Combine(product, "traces")
            };

            foreach (string existing in new[] { company }.Concat(managed))
            {
                if (Directory.Exists(existing) && (File.GetAttributes(existing) & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("'" + existing + "' is a reparse point; refusing to use it for Vestigo state.");
                }
            }

            // Before any ACL below is rewritten: SetAccessControl on a parent propagates to unprotected children, which
            // would hand a stale spool\helper (untrusted owner, inherited ACL) trust it has not earned. Refusals carry
            // the exact path and reason and fail the install before the service starts.
            string spool = Path.Combine(product, "spool");
            WindowsHelperDirectoryStore helperStore = new WindowsHelperDirectoryStore(session.Log);
            HelperDirectoryState helperState = HelperDirectoryProvisioning.Prepare(spool, helperStore);

            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            if (!Directory.Exists(company))
            {
                new DirectoryInfo(company).Create(CompanyFolderSecurity(system, administrators));
            }
            else
            {
                DirectoryInfo companyInfo = new DirectoryInfo(company);
                if (!IsTrustedOwner(companyInfo.GetAccessControl(), system, administrators))
                {
                    session.Log("Taking ownership of '" + company + "' (untrusted owner).");
                }

                companyInfo.SetAccessControl(CompanyFolderSecurity(system, administrators));
            }

            foreach (string directory in managed)
            {
                DirectorySecurity security = StateDirectorySecurity(system, administrators);
                DirectoryInfo info = new DirectoryInfo(directory);
                if (!info.Exists)
                {
                    info.Create(security);
                }
                else
                {
                    info.SetAccessControl(security);
                }
            }

            HelperDirectoryProvisioning.Complete(spool, helperStore, helperState);

            session.Log("Secured Vestigo state directories under " + product + ".");
        }

        private static bool IsTrustedOwner(FileSystemSecurity security, SecurityIdentifier system, SecurityIdentifier administrators)
        {
            SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner.Equals(system) || owner.Equals(administrators));
        }

        private static DirectorySecurity StateDirectorySecurity(SecurityIdentifier system, SecurityIdentifier administrators)
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        private static DirectorySecurity CompanyFolderSecurity(SecurityIdentifier system, SecurityIdentifier administrators)
        {
            DirectorySecurity security = StateDirectorySecurity(system, administrators);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        private static void SecureFile(string path)
        {
            new FileInfo(path).SetAccessControl(RestrictedSecurity());
        }

        private static FileSecurity RestrictedSecurity()
        {
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        private sealed class WindowsCredentialProviderStore : ICredentialProviderStore
        {
            public bool Exists(string path)
            {
                return File.Exists(path) || Directory.Exists(path);
            }

            public bool IsReparsePoint(string path)
            {
                if (File.Exists(path))
                {
                    return (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0;
                }

                if (Directory.Exists(path))
                {
                    return (new DirectoryInfo(path).Attributes & System.IO.FileAttributes.ReparsePoint) != 0;
                }

                return false;
            }

            public byte[] ReadAllBytes(string path)
            {
                return File.ReadAllBytes(path);
            }

            public void CreateDirectory(string path)
            {
                Directory.CreateDirectory(path);
            }

            public void WriteAllBytes(string path, byte[] content)
            {
                File.WriteAllBytes(path, content);
                FileInfo info = new FileInfo(path);
                info.SetAccessControl(RestrictedSecurity());
            }

            public bool TryDelete(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            public IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
            {
                if (!Directory.Exists(directory))
                {
                    return Array.Empty<string>();
                }

                return Directory.GetFiles(directory, pattern);
            }

            public string ReadInprocServer32()
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(CredentialProviderDeployment.InprocServer32Key, false))
                {
                    object value = key == null ? null : key.GetValue(null);
                    string path = value as string;
                    return string.IsNullOrWhiteSpace(path) ? null : path;
                }
            }

            public void WriteRegistration(string dllPath)
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey clsid = machine.CreateSubKey(CredentialProviderDeployment.ClassesClsidKey))
                    {
                        clsid.SetValue(null, "Vestigo Credential Provider", RegistryValueKind.String);
                    }

                    using (RegistryKey inproc = machine.CreateSubKey(CredentialProviderDeployment.InprocServer32Key))
                    {
                        inproc.SetValue(null, dllPath, RegistryValueKind.String);
                        inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
                    }

                    using (RegistryKey provider = machine.CreateSubKey(CredentialProviderDeployment.CredentialProvidersKey))
                    {
                        provider.SetValue(null, "Vestigo Credential Provider", RegistryValueKind.String);
                    }
                }
            }

            public void DeleteRegistration()
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    TryDeleteSubKey(machine, CredentialProviderDeployment.ClassesClsidKey);
                    TryDeleteSubKey(machine, CredentialProviderDeployment.CredentialProvidersKey);
                }
            }

            private static void TryDeleteSubKey(RegistryKey machine, string path)
            {
                try
                {
                    machine.DeleteSubKeyTree(path);
                }
                catch (ArgumentException)
                {
                }
            }
        }

        private sealed class SessionProperties : IInstallerProperties
        {
            private readonly Session _session;

            public SessionProperties(Session session)
            {
                _session = session;
            }

            public string Get(string name)
            {
                return _session[name] ?? string.Empty;
            }

            public void Set(string name, string value)
            {
                _session[name] = value ?? string.Empty;
            }
        }

        /// <summary>
        ///     Late-bound HNetCfg.FwPolicy2 store so the custom-action package does not take a firewall assembly dependency.
        /// </summary>
        private sealed class WindowsFirewallRuleStore : IFirewallRuleStore, IDisposable
        {
            private object _policy;
            private object _rules;

            public WindowsFirewallRuleStore()
            {
                Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                {
                    throw new InvalidOperationException("HNetCfg.FwPolicy2 is not available.");
                }

                _policy = Activator.CreateInstance(policyType);
                _rules = GetProperty(_policy, "Rules");
                if (_rules == null)
                {
                    throw new InvalidOperationException("The Windows Firewall policy did not return its rules collection.");
                }
            }

            public IReadOnlyList<FirewallRuleRecord> FindByName(string name)
            {
                List<FirewallRuleRecord> found = new List<FirewallRuleRecord>();
                if (TryCollectByName(name, found))
                {
                    return found;
                }

                object rule = null;
                try
                {
                    rule = TryItem(name);
                    if (rule == null)
                    {
                        return new FirewallRuleRecord[0];
                    }

                    return new[] { ReadRule(rule) };
                }
                finally
                {
                    Release(ref rule);
                }
            }

            public void Remove(string name)
            {
                Invoke(_rules, "Remove", new object[] { name }, BindingFlags.InvokeMethod);
            }

            public void Add(FirewallRuleRecord rule)
            {
                Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
                if (ruleType == null)
                {
                    throw new InvalidOperationException("HNetCfg.FWRule is not available.");
                }

                object comRule = Activator.CreateInstance(ruleType);
                try
                {
                    SetProperty(comRule, "Name", rule.Name);
                    SetProperty(comRule, "Description", rule.Description);
                    SetProperty(comRule, "Protocol", rule.Protocol);
                    SetProperty(comRule, "LocalPorts", rule.LocalPorts);
                    SetProperty(comRule, "RemoteAddresses", rule.RemoteAddresses);
                    SetProperty(comRule, "Direction", rule.Direction);
                    SetProperty(comRule, "Action", rule.Action);
                    SetProperty(comRule, "Profiles", rule.Profiles);
                    SetProperty(comRule, "InterfaceTypes", "All");
                    SetProperty(comRule, "Enabled", rule.Enabled);
                    Invoke(_rules, "Add", new object[] { comRule }, BindingFlags.InvokeMethod);
                }
                finally
                {
                    Release(ref comRule);
                }
            }

            public void Dispose()
            {
                Release(ref _rules);
                Release(ref _policy);
            }

            private static FirewallRuleRecord ReadRule(object rule)
            {
                return new FirewallRuleRecord(
                    GetString(rule, "Name"),
                    GetString(rule, "Description"),
                    GetInt(rule, "Protocol"),
                    GetString(rule, "LocalPorts"),
                    GetString(rule, "RemoteAddresses"),
                    GetInt(rule, "Direction"),
                    GetInt(rule, "Action"),
                    GetInt(rule, "Profiles"),
                    GetBool(rule, "Enabled"));
            }

            private bool TryCollectByName(string name, List<FirewallRuleRecord> found)
            {
                IEnumerable enumerable = _rules as IEnumerable;
                if (enumerable == null)
                {
                    return false;
                }

                IEnumerator enumerator = null;
                try
                {
                    enumerator = enumerable.GetEnumerator();
                    if (enumerator == null)
                    {
                        return false;
                    }

                    while (enumerator.MoveNext())
                    {
                        object rule = enumerator.Current;
                        try
                        {
                            if (rule != null && string.Equals(GetString(rule, "Name"), name, StringComparison.Ordinal))
                            {
                                found.Add(ReadRule(rule));
                            }
                        }
                        finally
                        {
                            Release(ref rule);
                        }
                    }

                    return true;
                }
                catch (Exception)
                {
                    found.Clear();
                    return false;
                }
                finally
                {
                    try
                    {
                        IDisposable disposable = enumerator as IDisposable;
                        if (disposable != null)
                        {
                            disposable.Dispose();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            private object TryItem(string name)
            {
                try
                {
                    return Invoke(_rules, "Item", new object[] { name }, BindingFlags.GetProperty);
                }
                catch (Exception ex) when (IsComFailure(ex))
                {
                }

                try
                {
                    return Invoke(_rules, "Item", new object[] { name }, BindingFlags.InvokeMethod);
                }
                catch (Exception ex) when (IsComFailure(ex))
                {
                    return null;
                }
            }

            private static object GetProperty(object target, string name)
            {
                return Invoke(target, name, null, BindingFlags.GetProperty);
            }

            private static void SetProperty(object target, string name, object value)
            {
                Invoke(target, name, new object[] { value }, BindingFlags.SetProperty);
            }

            private static object Invoke(object target, string name, object[] args, BindingFlags flags)
            {
                return target.GetType().InvokeMember(
                    name,
                    flags | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    args);
            }

            private static string GetString(object target, string name)
            {
                object value = GetProperty(target, name);
                return value == null ? string.Empty : value.ToString();
            }

            private static int GetInt(object target, string name)
            {
                object value = GetProperty(target, name);
                return value == null ? 0 : Convert.ToInt32(value);
            }

            private static bool GetBool(object target, string name)
            {
                object value = GetProperty(target, name);
                return value is bool flag && flag;
            }

            private static bool IsComFailure(Exception ex)
            {
                if (ex is COMException)
                {
                    return true;
                }

                TargetInvocationException invocation = ex as TargetInvocationException;
                return invocation != null && invocation.InnerException is COMException;
            }

            private static void Release(ref object com)
            {
                if (com != null && Marshal.IsComObject(com))
                {
                    Marshal.FinalReleaseComObject(com);
                }

                com = null;
            }
        }
    }
}