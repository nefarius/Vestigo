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
}
