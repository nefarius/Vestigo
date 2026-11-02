namespace Vestigo.Configuration;

/// <summary>
///     Operations a configured root permits.
/// </summary>
[Flags]
public enum RootAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4
}

/// <summary>
///     How Vestigo turns current drive volumes into named roots. Custom <see cref="VestigoOptions.Roots" /> stay additive.
/// </summary>
public enum VolumeRootMode
{
    None = 0,
    SystemDrive = 1,
    FixedVolumes = 2,
    AllVolumes = 3,
    AllVolumesLive = 4
}

/// <summary>Reserved names for volume roots generated from <see cref="VolumeRootMode" />.</summary>
public static class VolumeRootNames
{
    public const string Prefix = "drive-";

    /// <summary>True for generated names such as <c>drive-c</c>, not <c>drive-backups</c>.</summary>
    public static bool ConflictsWithGenerated(string name)
    {
        if (name.Length != Prefix.Length + 1 ||
            !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        char letter = char.ToLowerInvariant(name[Prefix.Length]);
        return letter is >= 'a' and <= 'z';
    }
}

/// <summary>
///     A named, access-controlled directory that clients may address as <c>name:relative\path</c>.
/// </summary>
public sealed class RootDefinition
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    public RootAccess Access { get; set; } = RootAccess.Read;
}

/// <summary>
///     Per-module switch read from <c>Vestigo:Modules:&lt;Name&gt;</c>.
/// </summary>
public sealed class ModuleSettings
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
///     File transfer limits read from <c>Vestigo:Files</c>.
/// </summary>
public sealed class FilesOptions
{
    public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>How long a one-time transfer ticket stays valid. At most 24 hours.</summary>
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
