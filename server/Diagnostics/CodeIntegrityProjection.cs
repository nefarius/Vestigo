namespace Vestigo.Diagnostics;

/// <summary>Names only documented lockdown and HVCI values, and matches a Code Integrity event to an image file name.</summary>
internal static class CodeIntegrityProjection
{
    public const int MaxTextChars = 260;

    public static string? LockdownName(int mode) => mode switch
    {
        0 => "unlocked",
        1 => "trial",
        2 => "locked",
        _ => null
    };

    public static bool? HvciEnabled(int value) => value switch
    {
        0 => false,
        1 => true,
        _ => null
    };

    public static string? ExpandImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        string text = imagePath.Trim();
        if (text.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end <= 1)
            {
                return null;
            }

            text = text[1..end];
        }
        else
        {
            int sys = text.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
            if (sys < 0)
            {
                return null;
            }

            text = text[..(sys + 4)];
        }

        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            string? root = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            text = root + text[@"\SystemRoot".Length..];
        }

        text = Environment.ExpandEnvironmentVariables(text);
        if (!text.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || text.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static string? ImageFileName(string? imagePath)
    {
        string? full = ExpandImagePath(imagePath);
        return full is null ? null : Path.GetFileName(full);
    }

    public static bool NamesFile(CodeIntegrityEvent entry, string? imageFileName)
    {
        if (string.IsNullOrEmpty(imageFileName))
        {
            return false;
        }

        if (entry.FileName is not null &&
            string.Equals(Path.GetFileName(entry.FileName), imageFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static CodeIntegrityEvent? ParseEvent(string? xml, int id, DateTimeOffset? at, string? message, string imageFileName)
    {
        string? file = Data(xml, "FileNameBuffer") ?? Data(xml, "FileName");
        bool named = file is not null
            ? string.Equals(Path.GetFileName(file), imageFileName, StringComparison.OrdinalIgnoreCase)
            : message?.Contains(imageFileName, StringComparison.OrdinalIgnoreCase) == true;

        if (!named)
        {
            return null;
        }

        return new CodeIntegrityEvent(
            id,
            at,
            Bound(file),
            Bound(Data(xml, "ProcessNameBuffer") ?? Data(xml, "ProcessName")),
            Bound(Data(xml, "RequestedPolicy")),
            Bound(Data(xml, "Status")));
    }

    public static string? Bound(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= MaxTextChars ? value : value[..MaxTextChars];
    }

    private static string? Data(string? xml, string name)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        string marker = "Name=\"" + name + "\"";
        int at = xml.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int start = xml.IndexOf('>', at + marker.Length);
        int end = start < 0 ? -1 : xml.IndexOf('<', start + 1);
        if (start < 0 || end <= start)
        {
            return null;
        }

        string value = xml[(start + 1)..end].Trim();
        return value.Length == 0 ? null : value;
    }
}
