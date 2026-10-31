using System.Reflection;
using System.Text;

namespace Vestigo.Configuration;

/// <summary>
///     Renders the checked-in configuration default table from <see cref="VestigoOptions" />.
/// </summary>
public static class ConfigurationCatalog
{
    public static string Markdown()
    {
        VestigoOptions options = new();
        StringBuilder builder = new();
        builder.AppendLine("# Configuration defaults");
        builder.AppendLine();
        builder.AppendLine("Generated from `VestigoOptions` property defaults. Do not edit by hand.");
        builder.AppendLine();
        builder.AppendLine("| Setting | Default |");
        builder.AppendLine("| --- | --- |");

        AppendRows(builder, options, string.Empty);

        builder.AppendLine();
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendRows(StringBuilder builder, object options, string prefix)
    {
        foreach (PropertyInfo property in options.GetType()
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanWrite)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            object? value = property.GetValue(options);
            if (value is not null && property.PropertyType.Namespace == typeof(VestigoOptions).Namespace
                && property.PropertyType.IsClass && property.PropertyType.Name.EndsWith("Options", StringComparison.Ordinal))
            {
                AppendRows(builder, value, prefix + property.Name + ":");
                continue;
            }

            builder.Append("| `")
                .Append(prefix)
                .Append(property.Name)
                .Append("` | `")
                .Append(Format(value))
                .AppendLine("` |");
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        FilesOptions files => $"MaxUploadBytes={files.MaxUploadBytes}",
        Vestigo.Devices.DevicesOptions => "defaults (see Devices:*)",
        Vestigo.Services.ServicesOptions => "defaults (see Services:*)",
        string text => text,
        IEnumerable<string> items => items.Any() ? string.Join(", ", items) : "empty",
        System.Collections.IEnumerable items when value is not string => items.Cast<object>().Any() ? "configured" : "empty",
        TimeSpan duration => duration.ToString(),
        ProcessesOptions processes => $"AllowPathLookup={processes.AllowPathLookup}",
        RemoteDebuggerOptions debugger => $"DefaultPort={debugger.DefaultPort}, InstallerDownloadTimeout={debugger.InstallerDownloadTimeout}, ExtraSearchPaths={(debugger.ExtraSearchPaths.Count == 0 ? "empty" : string.Join("; ", debugger.ExtraSearchPaths))}",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
