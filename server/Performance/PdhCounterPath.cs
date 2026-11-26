namespace Vestigo.Performance;

/// <summary>Validates English PDH counter paths supplied by a caller.</summary>
public static class PdhCounterPath
{
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "A counter path is required.";
        }

        if (path.Length > 1024 || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return "A counter path must be a single English PDH path.";
        }

        string trimmed = path.Trim();
        if (trimmed[0] != '\\')
        {
            return "A counter path must start with a backslash, for example \\Processor(_Total)\\% Processor Time.";
        }

        return null;
    }
}
