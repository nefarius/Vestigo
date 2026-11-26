using System.Runtime.InteropServices;

using WmiLight;

namespace Vestigo.Performance;

/// <summary>Bounded WmiLight queries. Missing namespaces or classes become empty lists.</summary>
internal static class WmiQuery
{
    public static IReadOnlyList<WmiObject> Query(string scope, string wql) =>
        Query(scope, wql, 256, out _, out _);

    public static IReadOnlyList<WmiObject> Query(string scope, string wql, int limit, out bool failed, out bool truncated)
    {
        failed = false;
        truncated = false;
        try
        {
            using WmiConnection connection = new(scope);
            List<WmiObject> rows = [];
            foreach (WmiObject row in connection.CreateQuery(wql, TimeSpan.FromSeconds(15)))
            {
                rows.Add(row);
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is WmiException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            failed = true;
            return [];
        }
    }

    public static string? String(WmiObject? row, string name)
    {
        if (row is null)
        {
            return null;
        }

        return StringRequired(row, name);
    }

    private static string? StringRequired(WmiObject row, string name)
    {
        try
        {
            object? value = row[name];
            return value switch
            {
                null => null,
                string text => string.IsNullOrWhiteSpace(text) ? null : text,
                string[] texts => texts.Length == 0 ? null : string.Join(", ", texts.Where(item => !string.IsNullOrWhiteSpace(item))),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is WmiException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    public static long? Int64(WmiObject row, string name)
    {
        try
        {
            object? value = row[name];
            return value switch
            {
                null => null,
                ulong number => number > long.MaxValue ? long.MaxValue : (long)number,
                long number => number,
                uint number => number,
                int number => number,
                ushort number => number,
                short number => number,
                byte number => number,
                _ => long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out long parsed) ? parsed : null
            };
        }
        catch (Exception ex) when (ex is WmiException or InvalidCastException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    public static int? Int32(WmiObject row, string name)
    {
        long? value = Int64(row, name);
        if (value is null || value < int.MinValue || value > int.MaxValue)
        {
            return null;
        }

        return (int)value.Value;
    }

    public static bool ContainsInt(WmiObject row, string name, int expected)
    {
        try
        {
            object? value = row[name];
            return value switch
            {
                null => false,
                int number => number == expected,
                uint number => number == expected,
                int[] numbers => numbers.Contains(expected),
                uint[] numbers => numbers.Contains((uint)expected),
                object[] items => items.Any(item => item is int i && i == expected || item is uint u && u == expected),
                _ => false
            };
        }
        catch (Exception ex) when (ex is WmiException or InvalidCastException or ArgumentException)
        {
            return false;
        }
    }

    public static bool? Boolean(WmiObject? row, string name)
    {
        if (row is null)
        {
            return null;
        }

        return BooleanRequired(row, name);
    }

    private static bool? BooleanRequired(WmiObject row, string name)
    {
        try
        {
            object? value = row[name];
            return value switch
            {
                null => null,
                bool flag => flag,
                int number => number != 0,
                uint number => number != 0,
                _ => null
            };
        }
        catch (Exception ex) when (ex is WmiException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
