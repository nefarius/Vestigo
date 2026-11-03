namespace Vestigo.Diagnostics;

/// <summary>The fixed machine, service-profile, and user WER directories. Callers never supply a path.</summary>
internal static class WindowsWerLocations
{
    public static IReadOnlyList<WerStore> Discover()
    {
        List<WerStore> stores = [];
        AddPair(stores, "machine", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), null);
        string system = Environment.SystemDirectory;
        string? windows = string.IsNullOrEmpty(system) ? null : Path.GetDirectoryName(system);
        if (!string.IsNullOrEmpty(windows))
        {
            AddPair(stores, "user", Path.Combine(windows, "System32", "config", "systemprofile", "AppData", "Local"), "SYSTEM");
            AddPair(stores, "user", Path.Combine(windows, "ServiceProfiles", "LocalService", "AppData", "Local"), "LOCAL SERVICE");
            AddPair(stores, "user", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local"), "NETWORK SERVICE");
            string? root = Path.GetPathRoot(windows);
            if (!string.IsNullOrEmpty(root))
            {
                AddUsers(stores, Path.Combine(root, "Users"));
            }
        }

        return stores;
    }

    private static void AddUsers(List<WerStore> stores, string users)
    {
        if (!Directory.Exists(users))
        {
            return;
        }

        IEnumerable<string> profiles;
        try
        {
            profiles = Directory.EnumerateDirectories(users);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string profile in profiles)
        {
            string name = Path.GetFileName(profile);
            if (Skip(name) || IsReparse(profile))
            {
                continue;
            }

            AddPair(stores, "user", Path.Combine(profile, "AppData", "Local"), name);
        }
    }

    private static void AddPair(List<WerStore> stores, string scope, string localAppData, string? profile)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return;
        }

        string wer = Path.Combine(localAppData, "Microsoft", "Windows", "WER");
        stores.Add(new WerStore(scope, "archive", Path.Combine(wer, "ReportArchive"), profile));
        stores.Add(new WerStore(scope, "queue", Path.Combine(wer, "ReportQueue"), profile));
    }

    private static bool Skip(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || name.Equals("All Users", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparse(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }
}
