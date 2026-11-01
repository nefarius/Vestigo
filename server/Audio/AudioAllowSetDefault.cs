using Microsoft.Extensions.Configuration;

using Vestigo.Configuration;

namespace Vestigo.Audio;

/// <summary>
///     Reads <c>Vestigo:Audio:AllowSetDefault</c> from the same JSON files as the server. Command-line arguments
///     and environment variables are not a source for this flag.
/// </summary>
internal static class AudioAllowSetDefault
{
    public static bool Load()
    {
        try
        {
            ConfigurationBuilder builder = new();
            AddJson(builder, Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            AddJson(builder, VestigoPaths.SettingsFilePath);
            return builder.Build().GetValue("Vestigo:Audio:AllowSetDefault", false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void AddJson(IConfigurationBuilder builder, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                builder.AddJsonFile(path, optional: true, reloadOnChange: false);
            }
        }
        catch (Exception)
        {
            // An unreadable machine settings file fails closed.
        }
    }
}
