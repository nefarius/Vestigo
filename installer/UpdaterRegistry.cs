using System.Text.Json;

namespace Vestigo.Installer;

sealed class UpdaterRegistry
{
    public required string Key { get; init; }

    public required string Value { get; init; }

    public static UpdaterRegistry Read(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement detection = document.RootElement.GetProperty("shared").GetProperty("detection");
        string? key = detection.GetProperty("key").GetString();
        string? value = detection.GetProperty("value").GetString();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Updater configuration '{path}' has no registry detection key.");
        }

        return new UpdaterRegistry { Key = key, Value = value };
    }
}
