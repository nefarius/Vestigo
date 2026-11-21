#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>One hashed live Credential Provider file plus COM registration. Tests supply fakes.</summary>
    public interface ICredentialProviderStore
    {
        bool Exists(string path);

        bool IsReparsePoint(string path);

        byte[] ReadAllBytes(string path);

        void CreateDirectory(string path);

        void WriteAllBytes(string path, byte[] content);

        bool TryDelete(string path);

        IReadOnlyList<string> EnumerateFiles(string directory, string pattern);

        string? ReadInprocServer32();

        void WriteRegistration(string dllPath);

        void DeleteRegistration();
    }

    /// <summary>Outcome of a staged-to-live deploy or remove. Never throws for a locked live DLL.</summary>
    public sealed class CredentialProviderDeployResult
    {
        public CredentialProviderDeployResult(string action, string livePath, bool registrationUpdated, IReadOnlyList<string> leftoverLocked)
        {
            Action = action;
            LivePath = livePath;
            RegistrationUpdated = registrationUpdated;
            LeftoverLocked = leftoverLocked;
        }

        public string Action { get; }

        public string LivePath { get; }

        public bool RegistrationUpdated { get; }

        public IReadOnlyList<string> LeftoverLocked { get; }
    }

    /// <summary>
    ///     Reboot-free Credential Provider install: MSI owns only the staging DLL. LogonUI loads a hash-suffixed
    ///     copy under live\. An unchanged hash leaves the mapped file and InprocServer32 alone.
    /// </summary>
    public static class CredentialProviderDeployment
    {
        public const string ClsidBraced = "{A3F1C8E2-4D7B-4E19-9C6A-8B2F1D5E7A90}";
        public const string StagingDirectoryName = "credential-provider";
        public const string LiveDirectoryName = "live";
        public const string StagingFileName = "Vestigo.CredentialProvider.dll";
        public const string ClassesClsidKey = @"SOFTWARE\Classes\CLSID\" + ClsidBraced;
        public const string InprocServer32Key = ClassesClsidKey + @"\InprocServer32";
        public const string CredentialProvidersKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\" + ClsidBraced;

        public static string StagingDirectory(string installDir)
        {
            return Path.Combine(installDir, StagingDirectoryName);
        }

        public static string StagingPath(string installDir)
        {
            return Path.Combine(StagingDirectory(installDir), StagingFileName);
        }

        public static string LiveDirectory(string installDir)
        {
            return Path.Combine(StagingDirectory(installDir), LiveDirectoryName);
        }

        public static string LiveFileName(string sha256Hex)
        {
            string prefix = sha256Hex.Length >= 16 ? sha256Hex.Substring(0, 16) : sha256Hex;
            return "Vestigo.CredentialProvider." + prefix.ToLowerInvariant() + ".dll";
        }

        public static string LivePath(string installDir, string sha256Hex)
        {
            return Path.Combine(LiveDirectory(installDir), LiveFileName(sha256Hex));
        }

        public static string HexSha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public static CredentialProviderDeployResult Deploy(string installDir, ICredentialProviderStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            RejectReparse(store, installDir);
            string staged = StagingPath(installDir);
            if (!store.Exists(staged))
            {
                throw new InvalidOperationException("Staged Credential Provider DLL was not found: " + staged);
            }

            RejectReparse(store, staged);
            byte[] bytes = store.ReadAllBytes(staged);
            string hash = HexSha256(bytes);
            string liveDir = LiveDirectory(installDir);
            RejectReparse(store, liveDir);
            string live = LivePath(installDir, hash);
            string? registered = store.ReadInprocServer32();
            bool liveMatches = store.Exists(live) && LiveBytesMatch(store, live, bytes);
            bool same = !string.IsNullOrWhiteSpace(registered) &&
                        string.Equals(registered, live, StringComparison.OrdinalIgnoreCase) &&
                        liveMatches;

            if (same)
            {
                store.WriteRegistration(live);
                IReadOnlyList<string> leftover = Sweep(store, liveDir, live);
                return new CredentialProviderDeployResult("unchanged", live, false, leftover);
            }

            store.CreateDirectory(liveDir);
            if (!liveMatches)
            {
                store.WriteAllBytes(live, bytes);
            }

            store.WriteRegistration(live);
            IReadOnlyList<string> locked = Sweep(store, liveDir, live);
            return new CredentialProviderDeployResult("rotated", live, true, locked);
        }

        public static CredentialProviderDeployResult Remove(string installDir, bool upgrading, ICredentialProviderStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (upgrading)
            {
                return new CredentialProviderDeployResult("upgrade-skip", store.ReadInprocServer32() ?? "", false, Array.Empty<string>());
            }

            store.DeleteRegistration();
            string liveDir = LiveDirectory(installDir);
            List<string> locked = new List<string>();
            if (store.Exists(liveDir) && !store.IsReparsePoint(liveDir))
            {
                foreach (string path in store.EnumerateFiles(liveDir, "Vestigo.CredentialProvider.*.dll"))
                {
                    if (!store.TryDelete(path))
                    {
                        locked.Add(path);
                    }
                }
            }

            return new CredentialProviderDeployResult("removed", "", true, locked);
        }

        private static IReadOnlyList<string> Sweep(ICredentialProviderStore store, string liveDir, string keep)
        {
            List<string> locked = new List<string>();
            if (!store.Exists(liveDir))
            {
                return locked;
            }

            foreach (string path in store.EnumerateFiles(liveDir, "Vestigo.CredentialProvider.*.dll"))
            {
                if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!store.TryDelete(path))
                {
                    locked.Add(path);
                }
            }

            return locked;
        }

        private static bool LiveBytesMatch(ICredentialProviderStore store, string live, byte[] expected)
        {
            byte[] existing = store.ReadAllBytes(live);
            if (existing == null || existing.Length != expected.Length)
            {
                return false;
            }

            for (int index = 0; index < existing.Length; index++)
            {
                if (existing[index] != expected[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static void RejectReparse(ICredentialProviderStore store, string path)
        {
            if (store.Exists(path) && store.IsReparsePoint(path))
            {
                throw new InvalidOperationException("'" + path + "' is a reparse point; refusing to use it for the Credential Provider.");
            }
        }
    }
}
