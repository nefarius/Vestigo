using System.Diagnostics.CodeAnalysis;
using System.IO;

using Microsoft.Extensions.Logging;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>A lettered volume that may become a named root.</summary>
public sealed record VolumeInfo(string RootPath, DriveType Type, bool IsReady);

/// <summary>Outcome of listing lettered volumes. <see cref="Volumes" /> is null when enumeration failed.</summary>
public sealed class VolumeEnumeration
{
    private VolumeEnumeration(IReadOnlyList<VolumeInfo>? volumes, Exception? error)
    {
        Volumes = volumes;
        Error = error;
    }

    public IReadOnlyList<VolumeInfo>? Volumes { get; }

    public Exception? Error { get; }

    public bool Failed => Volumes is null;

    public static VolumeEnumeration Succeeded(IReadOnlyList<VolumeInfo> volumes) => new(volumes, null);

    public static VolumeEnumeration FromError(Exception error) => new(null, error);
}

/// <summary>Enumerates current volumes and resolves the system drive. Tests substitute a fake.</summary>
public interface IVolumeEnumerator
{
    VolumeEnumeration Enumerate();

    string? ResolveSystemDrive();
}

/// <summary>Windows volumes from <see cref="DriveInfo" /> and the system drive from SpecialFolder / %SystemDrive%.</summary>
public sealed class DriveInfoVolumeEnumerator : IVolumeEnumerator
{
    public VolumeEnumeration Enumerate()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VolumeEnumeration.FromError(ex);
        }

        List<VolumeInfo> volumes = [];
        foreach (DriveInfo drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            volumes.Add(new VolumeInfo(drive.Name, drive.DriveType, ready));
        }

        return VolumeEnumeration.Succeeded(volumes);
    }

    public string? ResolveSystemDrive()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            string? root = Path.GetPathRoot(windows);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        string? systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(systemDrive))
        {
            return null;
        }

        string candidate = systemDrive.EndsWith(':') ? systemDrive + Path.DirectorySeparatorChar : systemDrive;
        string? pathRoot = Path.GetPathRoot(candidate);
        return string.IsNullOrWhiteSpace(pathRoot) ? null : pathRoot;
    }
}

/// <summary>
///     Turns <see cref="VestigoOptions.VolumeRoots" /> into named <c>drive-*</c> roots. Snapshot modes resolve once;
///     <see cref="VolumeRootMode.AllVolumesLive" /> re-enumerates behind a short cache.
/// </summary>
public sealed class VolumeRootSource
{
    public static readonly TimeSpan LiveCacheDuration = TimeSpan.FromSeconds(2);

    private static readonly DriveType[] AllVolumeTypes =
    [
        DriveType.Fixed,
        DriveType.Removable,
        DriveType.Network,
        DriveType.CDRom,
        DriveType.Ram
    ];

    private readonly VestigoOptions _options;
    private readonly IVolumeEnumerator _enumerator;
    private readonly TimeProvider _clock;
    private readonly ILogger<VolumeRootSource>? _logger;
    private readonly object _gate = new();
    private IReadOnlyList<RootDefinition>? _snapshot;
    private IReadOnlyList<RootDefinition>? _cached;
    private DateTimeOffset _cachedAt;

    public VolumeRootSource(VestigoOptions options)
        : this(options, new DriveInfoVolumeEnumerator(), TimeProvider.System)
    {
    }

    public VolumeRootSource(VestigoOptions options, IVolumeEnumerator enumerator)
        : this(options, enumerator, TimeProvider.System)
    {
    }

    public VolumeRootSource(VestigoOptions options, IVolumeEnumerator enumerator, ILogger<VolumeRootSource> logger)
        : this(options, enumerator, TimeProvider.System, logger)
    {
    }

    public VolumeRootSource(
        VestigoOptions options,
        IVolumeEnumerator enumerator,
        TimeProvider? clock,
        ILogger<VolumeRootSource>? logger = null)
    {
        _options = options;
        _enumerator = enumerator;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        if (options.VolumeRoots is not VolumeRootMode.None and not VolumeRootMode.AllVolumesLive)
        {
            _snapshot = TryBuild();
        }
    }

    /// <summary>Generated volume roots for the configured mode. Does not include custom <see cref="VestigoOptions.Roots" />.</summary>
    public IReadOnlyList<RootDefinition> Current
    {
        get
        {
            if (_options.VolumeRoots == VolumeRootMode.None)
            {
                return [];
            }

            if (_options.VolumeRoots != VolumeRootMode.AllVolumesLive)
            {
                IReadOnlyList<RootDefinition>? snapshot = _snapshot;
                if (snapshot is not null)
                {
                    return snapshot;
                }

                lock (_gate)
                {
                    if (_snapshot is not null)
                    {
                        return _snapshot;
                    }

                    IReadOnlyList<RootDefinition>? built = TryBuild();
                    if (built is not null)
                    {
                        _snapshot = built;
                    }

                    return built ?? [];
                }
            }

            lock (_gate)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                if (_cached is not null && now - _cachedAt < LiveCacheDuration)
                {
                    return _cached;
                }

                IReadOnlyList<RootDefinition>? built = TryBuild();
                if (built is null)
                {
                    return _cached ?? [];
                }

                _cached = built;
                _cachedAt = now;
                return built;
            }
        }
    }

    private IReadOnlyList<RootDefinition>? TryBuild()
    {
        if (_options.VolumeRoots == VolumeRootMode.None)
        {
            return [];
        }

        List<RootDefinition> roots = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        if (_options.VolumeRoots == VolumeRootMode.SystemDrive)
        {
            TryAdd(roots, names, _enumerator.ResolveSystemDrive());
            return roots;
        }

        VolumeEnumeration enumeration = _enumerator.Enumerate();
        if (enumeration.Failed)
        {
            _logger?.LogWarning(
                enumeration.Error,
                "Failed to enumerate drive volumes for Vestigo:VolumeRoots {Mode}.",
                _options.VolumeRoots);
            return null;
        }

        bool includeAll = _options.VolumeRoots is VolumeRootMode.AllVolumes or VolumeRootMode.AllVolumesLive;
        foreach (VolumeInfo volume in enumeration.Volumes!)
        {
            if (!volume.IsReady)
            {
                continue;
            }

            if (includeAll)
            {
                if (Array.IndexOf(AllVolumeTypes, volume.Type) < 0)
                {
                    continue;
                }
            }
            else if (volume.Type != DriveType.Fixed)
            {
                continue;
            }

            TryAdd(roots, names, volume.RootPath);
        }

        return roots;
    }

    private void TryAdd(List<RootDefinition> roots, HashSet<string> names, string? path)
    {
        if (!TryCreate(path, out RootDefinition? root) || !names.Add(root.Name))
        {
            return;
        }

        roots.Add(root);
    }

    private bool TryCreate(string? path, [NotNullWhen(true)] out RootDefinition? root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string? name = NameFor(full);
        if (name is null)
        {
            return false;
        }

        root = new RootDefinition
        {
            Name = name,
            Path = Path.GetPathRoot(full) is { } driveRoot &&
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(full),
                       Path.TrimEndingDirectorySeparator(driveRoot),
                       StringComparison.OrdinalIgnoreCase)
                ? driveRoot
                : Path.TrimEndingDirectorySeparator(full),
            Access = _options.VolumeRootAccess
        };
        return true;
    }

    internal static string? NameFor(string path)
    {
        string? driveRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(driveRoot))
        {
            return null;
        }

        string trimmed = driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length != 2 || trimmed[1] != ':')
        {
            return null;
        }

        char letter = char.ToLowerInvariant(trimmed[0]);
        return letter is >= 'a' and <= 'z' ? VolumeRootNames.Prefix + letter : null;
    }
}
