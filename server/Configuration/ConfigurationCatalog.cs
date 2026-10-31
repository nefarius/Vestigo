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

        foreach (PropertyInfo property in typeof(VestigoOptions)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanWrite)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            builder.Append("| `")
                .Append(property.Name)
                .Append("` | `")
                .Append(Format(property.GetValue(options)))
                .AppendLine("` |");
        }

        builder.AppendLine();
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => text,
        IEnumerable<string> items => items.Any() ? string.Join(", ", items) : "empty",
        TimeSpan duration => duration.ToString(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
