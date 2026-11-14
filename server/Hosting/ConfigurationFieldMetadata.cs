using System.Reflection;
using Vestigo.Configuration;

namespace Vestigo.Hosting;

internal static class ConfigurationFieldMetadata
{
    public static Type? TypeFor(string path)
    {
        Type? type = typeof(VestigoOptions);
        foreach (string part in path.Split(':'))
        {
            PropertyInfo? property = type?.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            type = property?.PropertyType;
        }
        return type;
    }

    public static bool IsStringList(string path) =>
        TypeFor(path) is Type type && type != typeof(string) && typeof(IEnumerable<string>).IsAssignableFrom(type);
}
