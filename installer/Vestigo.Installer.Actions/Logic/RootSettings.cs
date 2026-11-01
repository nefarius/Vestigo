#nullable enable
using System;
using System.Collections.Generic;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>Existing volume-root settings read from the machine appsettings.json.</summary>
    public sealed class ExistingRootSettings
    {
        public ExistingRootSettings(string? volumeRoots, string? volumeRootAccess, bool hasCustomRoots)
        {
            VolumeRoots = volumeRoots;
            VolumeRootAccess = volumeRootAccess;
            HasCustomRoots = hasCustomRoots;
        }

        /// <summary>Server enum name such as <c>FixedVolumes</c>, or null when the key is missing.</summary>
        public string? VolumeRoots { get; }

        /// <summary>Server flags text such as <c>Read, Write, Execute</c>, or null when the key is missing.</summary>
        public string? VolumeRootAccess { get; }

        /// <summary>True when <c>Vestigo:Roots</c> contains at least one object.</summary>
        public bool HasCustomRoots { get; }
    }

    /// <summary>Validated installer roots choices, ready to be written to appsettings.json.</summary>
    public sealed class ResolvedRootSettings
    {
        private readonly string? _persistedAccess;

        public ResolvedRootSettings(string mode, string access)
            : this(mode, access, null)
        {
        }

        public ResolvedRootSettings(string mode, string access, string? persistedAccess)
        {
            Mode = mode;
            Access = access;
            _persistedAccess = persistedAccess;
        }

        /// <summary><see cref="RootSettings.ModeCustom" />, <see cref="RootSettings.ModeSystem" />, <see cref="RootSettings.ModeFixed" />, <see cref="RootSettings.ModeAll" />, or <see cref="RootSettings.ModeLive" />.</summary>
        public string Mode { get; }

        /// <summary><see cref="RootSettings.AccessRead" />, <see cref="RootSettings.AccessReadWrite" />, or <see cref="RootSettings.AccessReadWriteExecute" />.</summary>
        public string Access { get; }

        public string VolumeRootsValue
        {
            get
            {
                if (Mode == RootSettings.ModeCustom)
                {
                    return RootSettings.ServerNone;
                }

                if (Mode == RootSettings.ModeSystem)
                {
                    return RootSettings.ServerSystemDrive;
                }

                if (Mode == RootSettings.ModeFixed)
                {
                    return RootSettings.ServerFixedVolumes;
                }

                if (Mode == RootSettings.ModeAll)
                {
                    return RootSettings.ServerAllVolumes;
                }

                return RootSettings.ServerAllVolumesLive;
            }
        }

        public string VolumeRootAccessValue
        {
            get
            {
                if (_persistedAccess != null)
                {
                    return _persistedAccess;
                }

                if (Access == RootSettings.AccessRead)
                {
                    return RootSettings.ServerRead;
                }

                if (Access == RootSettings.AccessReadWrite)
                {
                    return RootSettings.ServerReadWrite;
                }

                return RootSettings.ServerReadWriteExecute;
            }
        }
    }

    /// <summary>
    ///     Pure installer logic for the file-system roots pages: mode mapping, validation, and the appsettings.json merge
    ///     of <c>Vestigo:VolumeRoots</c> and <c>Vestigo:VolumeRootAccess</c>.
    /// </summary>
    public static class RootSettings
    {
        public const string ModeCustom = "custom";
        public const string ModeSystem = "system";
        public const string ModeFixed = "fixed";
        public const string ModeAll = "all";
        public const string ModeLive = "live";

        public const string AccessRead = "r";
        public const string AccessReadWrite = "rw";
        public const string AccessReadWriteExecute = "rwx";

        public const string ServerNone = "None";
        public const string ServerSystemDrive = "SystemDrive";
        public const string ServerFixedVolumes = "FixedVolumes";
        public const string ServerAllVolumes = "AllVolumes";
        public const string ServerAllVolumesLive = "AllVolumesLive";

        public const string ServerRead = "Read";
        public const string ServerReadWrite = "Read, Write";
        public const string ServerReadWriteExecute = "Read, Write, Execute";

        /// <summary>Reads the volume-root keys. Returns null when the text is missing or not parseable JSON.</summary>
        public static ExistingRootSettings? ReadExisting(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            object? root;
            try
            {
                root = MiniJson.Parse(json!);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!(root is JsonObject rootObject) ||
                !rootObject.TryGet(ListenSettings.SectionName, out object? section) ||
                !(section is JsonObject vestigo))
            {
                return null;
            }

            string? volumeRoots = null;
            if (vestigo.TryGetIgnoreCase("VolumeRoots", out object? mode) && mode is string modeText &&
                modeText.Trim().Length > 0)
            {
                volumeRoots = modeText.Trim();
            }

            string? volumeRootAccess = null;
            if (vestigo.TryGetIgnoreCase("VolumeRootAccess", out object? access) && access is string accessText &&
                accessText.Trim().Length > 0)
            {
                volumeRootAccess = accessText.Trim();
            }

            bool hasCustomRoots = false;
            if (vestigo.TryGetIgnoreCase("Roots", out object? roots) && roots is List<object?> list)
            {
                foreach (object? item in list)
                {
                    if (item is JsonObject)
                    {
                        hasCustomRoots = true;
                        break;
                    }
                }
            }

            return new ExistingRootSettings(volumeRoots, volumeRootAccess, hasCustomRoots);
        }

        /// <summary>
        ///     Combines explicit MSI property values with the existing file and defaults. Blank inputs fall back to the
        ///     existing value, then to <see cref="ModeFixed" /> and <see cref="AccessReadWriteExecute" />. On an upgrade
        ///     where the file has no <c>VolumeRoots</c> key but does have custom <c>Roots</c>, the default mode is
        ///     <see cref="ModeCustom" />. A blank access property keeps the existing flags text. Throws
        ///     <see cref="ArgumentException" /> for invalid values.
        /// </summary>
        public static ResolvedRootSettings Resolve(
            string? mode,
            string? access,
            ExistingRootSettings? existing,
            bool isUpgrade = false)
        {
            string defaultMode = ModeFixed;
            if (existing != null)
            {
                string? mapped = MapServerMode(existing.VolumeRoots);
                if (mapped != null)
                {
                    defaultMode = mapped;
                }
                else if (isUpgrade && existing.VolumeRoots == null && existing.HasCustomRoots)
                {
                    defaultMode = ModeCustom;
                }
            }

            string? existingAccess = existing != null ? existing.VolumeRootAccess : null;
            string? mappedAccess = MapServerAccess(existingAccess);
            string defaultAccess = mappedAccess ?? AccessReadWriteExecute;
            string resolvedMode = Normalize(
                mode,
                "VESTIGO_ROOTS_MODE",
                defaultMode,
                ModeCustom,
                ModeSystem,
                ModeFixed,
                ModeAll,
                ModeLive);
            string resolvedAccess = Normalize(
                access,
                "VESTIGO_ROOTS_ACCESS",
                defaultAccess,
                AccessRead,
                AccessReadWrite,
                AccessReadWriteExecute);
            string? persistedAccess = null;
            if (string.IsNullOrWhiteSpace(access) ||
                (mappedAccess != null && string.Equals(resolvedAccess, mappedAccess, StringComparison.Ordinal)))
            {
                persistedAccess = existingAccess;
            }

            return new ResolvedRootSettings(resolvedMode, resolvedAccess, persistedAccess);
        }

        /// <summary>
        ///     Writes <c>Vestigo:VolumeRoots</c> and <c>Vestigo:VolumeRootAccess</c>, keeping every other member (including
        ///     the original key casing).
        /// </summary>
        public static void Apply(JsonObject vestigo, ResolvedRootSettings settings)
        {
            vestigo.SetIgnoreCase("VolumeRoots", settings.VolumeRootsValue);
            vestigo.SetIgnoreCase("VolumeRootAccess", settings.VolumeRootAccessValue);
        }

        private static string? MapServerMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string trimmed = value!.Trim();
            if (string.Equals(trimmed, ServerNone, StringComparison.OrdinalIgnoreCase))
            {
                return ModeCustom;
            }

            if (string.Equals(trimmed, ServerSystemDrive, StringComparison.OrdinalIgnoreCase))
            {
                return ModeSystem;
            }

            if (string.Equals(trimmed, ServerFixedVolumes, StringComparison.OrdinalIgnoreCase))
            {
                return ModeFixed;
            }

            if (string.Equals(trimmed, ServerAllVolumes, StringComparison.OrdinalIgnoreCase))
            {
                return ModeAll;
            }

            if (string.Equals(trimmed, ServerAllVolumesLive, StringComparison.OrdinalIgnoreCase))
            {
                return ModeLive;
            }

            return null;
        }

        private static string? MapServerAccess(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            bool read = ContainsFlag(value!, "Read");
            bool write = ContainsFlag(value!, "Write");
            bool execute = ContainsFlag(value!, "Execute");
            if (!read && !write && !execute)
            {
                return null;
            }

            if (execute)
            {
                return AccessReadWriteExecute;
            }

            return write ? AccessReadWrite : AccessRead;
        }

        private static bool ContainsFlag(string value, string flag)
        {
            foreach (string part in value.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(part.Trim(), flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string? value, string propertyName, string fallback, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            string trimmed = value!.Trim();
            foreach (string candidate in allowed)
            {
                if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            throw new ArgumentException(propertyName + " must be one of: " + string.Join(", ", allowed) + ".");
        }
    }
}
