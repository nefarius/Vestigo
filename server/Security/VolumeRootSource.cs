using System.Diagnostics.CodeAnalysis;
using System.IO;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>A lettered volume that may become a named root.</summary>
public sealed record VolumeInfo(string RootPath, DriveType Type, bool IsReady);

/// <summary>Enumerates current volumes and resolves the system drive. Tests substitute a fake.</summary>
public interface IVolumeEnumerator
{
    IReadOnlyList<VolumeInfo> Enumerate();

    string? ResolveSystemDrive();
}

/// <summary>Windows volumes from <see cref="DriveInfo" /> and the system drive from SpecialFolder / %SystemDrive%.</summary>
public sealed class DriveInfoVolumeEnumerator : IVolumeEnumerator
{
    public IReadOnlyList<VolumeInfo> Enumerate()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

        return volumes;
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
    private readonly object _gate = new();
    private readonly IReadOnlyList<RootDefinition>? _snapshot;
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

    public VolumeRootSource(VestigoOptions options, IVolumeEnumerator enumerator, TimeProvider? clock)
    {
        _options = options;
        _enumerator = enumerator;
        _clock = clock ?? TimeProvider.System;
        if (options.VolumeRoots != VolumeRootMode.AllVolumesLive)
        {
            _snapshot = Build();
        }
    }

    /// <summary>Generated volume roots for the configured mode. Does not include custom <see cref="VestigoOptions.Roots" />.</summary>
    public IReadOnlyList<RootDefinition> Current
    {
        get
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            lock (_gate)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                if (_cached is not null && now - _cachedAt < LiveCacheDuration)
                {
                    return _cached;
                }

                _cached = Build();
                _cachedAt = now;
                return _cached;
            }
        }
    }

    private IReadOnlyList<RootDefinition> Build()
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

        bool includeAll = _options.VolumeRoots is VolumeRootMode.AllVolumes or VolumeRootMode.AllVolumesLive;
        foreach (VolumeInfo volume in _enumerator.Enumerate())
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
