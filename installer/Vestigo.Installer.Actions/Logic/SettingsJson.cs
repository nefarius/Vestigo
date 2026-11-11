#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>JSON syntax comes from System.Text.Json; these helpers preserve the installer merge conventions.</summary>
    public static class SettingsJson
    {
        public static JsonNode? Parse(string text)
        {
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                }))
                {
                    return ToNode(document.RootElement);
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                // Token parsing accepts escaped unpaired surrogates; decoding a property name
                // or string value then throws InvalidOperationException. Keep those failures
                // on the existing unreadable-settings/backup-and-recreate path as well.
                throw new FormatException("The settings file is not valid JSON.", ex);
            }
        }

        private static JsonNode? ToNode(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                JsonObject result = new JsonObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    // Match the old merge semantics: the last exact duplicate wins, at the first key's position.
                    result[property.Name] = ToNode(property.Value);
                }

                return result;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                JsonArray result = new JsonArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    result.Add(ToNode(item));
                }

                return result;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                // Decode eagerly, even in unrelated settings, so invalid Unicode cannot
                // survive conversion and fail later outside Parse's FormatException boundary.
                return JsonValue.Create(element.GetString());
            }

            // Retain numbers as JSON tokens rather than converting through double/decimal.
            return element.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(element.Clone());
        }

        public static string Write(JsonNode? value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    NewLine = "\n"
                }))
                {
                    if (value == null) writer.WriteNullValue();
                    else value.WriteTo(writer);
                }

                return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
            }
        }

        public static string? StringValue(this JsonNode? node) =>
            node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        public static bool TryGetIgnoreCase(this JsonObject obj, string name, out JsonNode? value)
        {
            foreach (KeyValuePair<string, JsonNode?> member in obj)
            {
                if (string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = member.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public static void SetIgnoreCase(this JsonObject obj, string name, JsonNode? value)
        {
            foreach (KeyValuePair<string, JsonNode?> member in obj)
            {
                if (string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    name = member.Key;
                    break;
                }
            }

            obj[name] = value;
        }
    }
}
