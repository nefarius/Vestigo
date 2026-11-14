using System.Reflection;
using System.Text;

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

    public static bool IsDuration(string path) => TypeFor(path) is Type type && Unwrap(type) == typeof(TimeSpan);

    public static bool IsNamedRoots(string path) => ElementType(TypeFor(path)) == typeof(RootDefinition);

    public static bool IsStringDictionary(string path)
    {
        Type? type = TypeFor(path);
        return type is not null && (IsStringMap(type) || type.GetInterfaces().Any(IsStringMap));
    }

    public static bool IsByteCount(string path, out Type integerType)
    {
        integerType = typeof(long);
        if (Leaf(path) is not PropertyInfo property) return false;
        Type type = Unwrap(property.PropertyType);
        if (!property.Name.EndsWith("Bytes", StringComparison.OrdinalIgnoreCase) || !IsIntegral(type)) return false;
        integerType = type;
        return true;
    }

    public static bool TryGetEnum(string path, out Type enumType, out bool flags)
    {
        Type? type = TypeFor(path) is Type resolved ? Unwrap(resolved) : null;
        if (type is not { IsEnum: true })
        {
            enumType = typeof(object);
            flags = false;
            return false;
        }

        enumType = type;
        flags = type.IsDefined(typeof(FlagsAttribute), inherit: false);
        return true;
    }

    /// <summary>Explains an empty optional path that falls back to a tool installed next to Vestigo.exe.</summary>
    public static string? BundledExecutableHint(string path) => path switch
    {
        "Devices:NefconPath" => @"Leave empty to use tools\nefconc.exe next to Vestigo.exe.",
        "DebugOutput:ExecutablePath" => @"Leave empty to use tools\DebugViewConsole.exe next to Vestigo.exe.",
        "Logs:RipgrepPath" => @"Leave empty to use tools\rg.exe next to Vestigo.exe.",
        _ => null
    };

    /// <summary>Turns a PascalCase enum name into display text without changing the saved name.</summary>
    public static string EnumDisplayLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        StringBuilder builder = new(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && NeedsSpace(name, index)) builder.Append(' ');
            builder.Append(current);
        }
        return builder.ToString();
    }

    private static bool NeedsSpace(string name, int index)
    {
        char current = name[index];
        char previous = name[index - 1];
        if (char.IsDigit(current)) return char.IsLetter(previous);
        if (!char.IsUpper(current)) return false;
        if (char.IsLower(previous)) return true;
        return index + 1 < name.Length && char.IsLower(name[index + 1]) && char.IsUpper(previous);
    }

    private static PropertyInfo? Leaf(string path)
    {
        Type? type = typeof(VestigoOptions);
        PropertyInfo? property = null;
        foreach (string part in path.Split(':'))
        {
            property = type?.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            type = property?.PropertyType;
            if (type is null) return null;
        }
        return property;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsIntegral(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static bool IsStringMap(Type type)
    {
        if (!type.IsGenericType) return false;
        Type definition = type.GetGenericTypeDefinition();
        if (definition != typeof(Dictionary<,>) && definition != typeof(IDictionary<,>)) return false;
        Type[] arguments = type.GetGenericArguments();
        return arguments[0] == typeof(string) && arguments[1] == typeof(string);
    }

    private static Type? ElementType(Type? type)
    {
        if (type is null || type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        return type.GetInterfaces().FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
