using System.Reflection;

using Microsoft.Win32;

namespace Vestigo.Updates;

/// <summary>Reads the installed Vestigo version from the updater registry value or the entry assembly.</summary>
public interface IInstalledVersionSource
{
    InstalledVersion Read();
}

/// <summary>
///     Prefers <c>HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version</c> (the same value Vicius
///     compares) and falls back to the entry assembly informational version when the Updater feature is absent.
/// </summary>
public sealed class InstalledVersionReader : IInstalledVersionSource
{
    private readonly Func<string?> _registry;
    private readonly Func<string?> _assembly;

    public InstalledVersionReader()
        : this(ReadRegistry, ReadAssembly)
    {
    }

    internal InstalledVersionReader(Func<string?> registry, Func<string?> assembly)
    {
        _registry = registry;
        _assembly = assembly;
    }

    public InstalledVersion Read()
    {
        if (Normalize(_registry()) is { } registry)
        {
            return new InstalledVersion(registry, "registry");
        }

        if (Normalize(_assembly()) is { } assembly)
        {
            return new InstalledVersion(assembly, "assembly");
        }

        return new InstalledVersion("0.0.0.0", "unknown");
    }

    private static string? ReadRegistry()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(UpdateEndpoints.RegistryKey);
            return key?.GetValue(UpdateEndpoints.RegistryValue) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? ReadAssembly()
    {
        Assembly? assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString();
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}
