#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>Existing Vestigo listen settings read from the machine appsettings.json.</summary>
    public sealed class ExistingSettings
    {
        public ExistingSettings(string? address, int? port, IReadOnlyList<string> tokens)
        {
            Address = address;
            Port = port;
            Tokens = tokens;
        }

        public string? Address { get; }

        public int? Port { get; }

        public IReadOnlyList<string> Tokens { get; }
    }

    /// <summary>Validated installer choices, ready to be written to appsettings.json.</summary>
    public sealed class ResolvedSettings
    {
        public ResolvedSettings(string listenMode, string authMode, int port, string token, string? existingAddress)
        {
            ListenMode = listenMode;
            AuthMode = authMode;
            Port = port;
            Token = token;
            ExistingAddress = existingAddress;
        }

        /// <summary><see cref="ListenSettings.ListenLocalhost" /> or <see cref="ListenSettings.ListenAny" />.</summary>
        public string ListenMode { get; }

        /// <summary><see cref="ListenSettings.AuthNone" /> or <see cref="ListenSettings.AuthToken" />.</summary>
        public string AuthMode { get; }

        public int Port { get; }

        /// <summary>Empty unless <see cref="AuthMode" /> is token.</summary>
        public string Token { get; }

        /// <summary>Address already in the file; kept when it still matches <see cref="ListenMode" />.</summary>
        public string? ExistingAddress { get; }

        public bool BindsAny => ListenMode == ListenSettings.ListenAny;

        public int UiChoice => ListenSettings.ToUiChoice(ListenMode, AuthMode);
    }

    /// <summary>
    ///     Pure installer logic: mode mapping, validation, token generation, and the appsettings.json merge. It is compiled
    ///     into the net48 custom action assembly and into the test project.
    /// </summary>
    public static class ListenSettings
    {
        public const string ListenLocalhost = "localhost";
        public const string ListenAny = "any";
        public const string AuthNone = "none";
        public const string AuthToken = "token";
        public const int DefaultPort = 5098;
        public const int MinimumTokenLength = 16;
        public const string LoopbackAddress = "127.0.0.1";
        public const string AnyAddress = "0.0.0.0";
        public const string SectionName = "Vestigo";

        /// <summary>Radio-button choice: 1 localhost/no token, 2 localhost/token, 3 any/no token, 4 any/token.</summary>
        public static int ToUiChoice(string listenMode, string authMode)
        {
            bool any = listenMode == ListenAny;
            bool token = authMode == AuthToken;
            return (any ? 3 : 1) + (token ? 1 : 0);
        }

        public static void FromUiChoice(int choice, out string listenMode, out string authMode)
        {
            if (choice < 1 || choice > 4)
            {
                throw new ArgumentException("Choose one of the four listen modes.");
            }

            listenMode = choice >= 3 ? ListenAny : ListenLocalhost;
            authMode = choice % 2 == 0 ? AuthToken : AuthNone;
        }

        /// <summary>32 random bytes as unpadded base64url (43 characters).</summary>
        public static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static bool IsLoopbackAddress(string? address)
        {
            return address != null && IPAddress.TryParse(address, out IPAddress? ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>Maps an existing address and token presence onto the four modes.</summary>
        public static void MapExisting(ExistingSettings existing, out string listenMode, out string authMode)
        {
            bool parsed = existing.Address != null && IPAddress.TryParse(existing.Address, out _);
            listenMode = !parsed || IsLoopbackAddress(existing.Address) ? ListenLocalhost : ListenAny;
            authMode = existing.Tokens.Count > 0 ? AuthToken : AuthNone;
        }

        /// <summary>Reads the Vestigo listen keys. Returns null when the text is missing or not parseable JSON.</summary>
        public static ExistingSettings? ReadExisting(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            object? root;
            try
            {
                root = MiniJson.Parse(json!);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!(root is JsonObject rootObject) ||
                !rootObject.TryGet(SectionName, out object? section) ||
                !(section is JsonObject vestigo))
            {
                return null;
            }

            string? address = vestigo.TryGet("ListenAddress", out object? a) ? a as string : null;
            int? port = null;
            if (vestigo.TryGet("ListenPort", out object? p) && p is JsonNumber number &&
                int.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort))
            {
                port = parsedPort;
            }

            List<string> tokens = new List<string>();
            if (vestigo.TryGet("ApiTokens", out object? t) && t is List<object?> list)
            {
                foreach (object? item in list)
                {
                    if (item is string text && text.Trim().Length > 0)
                    {
                        tokens.Add(text);
                    }
                }
            }

            return new ExistingSettings(address, port, tokens);
        }

        /// <summary>
        ///     Combines explicit MSI property values with the existing file and defaults. Blank inputs fall back to the
        ///     existing value, then to the default (any address, token, port 5098; on an upgrade with no readable settings, localhost without a token so an existing loopback setup is never widened). A missing token in token mode keeps the
        ///     existing token or generates a new one. When <paramref name="tokenIsSuggestion" /> is true the token is only an
        ///     auto-suggested value the user never edited, so an existing token wins. Throws <see cref="ArgumentException" /> for invalid values.
        /// </summary>
        public static ResolvedSettings Resolve(
            string? listenMode,
            string? authMode,
            string? portText,
            string? token,
            ExistingSettings? existing,
            bool tokenIsSuggestion = false,
            bool isUpgrade = false)
        {
            string? mappedListen = null;
            string? mappedAuth = null;
            if (existing != null)
            {
                MapExisting(existing, out string l, out string a);
                mappedListen = l;
                mappedAuth = a;
            }

            string listen = Normalize(listenMode, "VESTIGO_LISTEN_MODE", mappedListen ?? (isUpgrade ? ListenLocalhost : ListenAny), ListenLocalhost, ListenAny);
            string auth = Normalize(authMode, "VESTIGO_AUTH_MODE", mappedAuth ?? (isUpgrade ? AuthNone : AuthToken), AuthNone, AuthToken);

            int port = existing?.Port is int existingPort && IsValidPort(existingPort) ? existingPort : DefaultPort;
            if (!string.IsNullOrWhiteSpace(portText))
            {
                port = ParsePort(portText!);
            }

            string resolvedToken = string.Empty;
            if (auth == AuthToken)
            {
                string requested = (token ?? string.Empty).Trim();
                if (requested.Length == 0)
                {
                    requested = existing != null && existing.Tokens.Count > 0 ? existing.Tokens[0] : GenerateToken();
                }
                else if (tokenIsSuggestion && existing != null && existing.Tokens.Count > 0)
                {
                    requested = existing.Tokens[0];
                }

                string? tokenError = ValidateToken(requested);
                if (tokenError != null)
                {
                    throw new ArgumentException(tokenError);
                }

                resolvedToken = requested;
            }

            return new ResolvedSettings(listen, auth, port, resolvedToken, existing?.Address);
        }

        public static int ParsePort(string text)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || !IsValidPort(port))
            {
                throw new ArgumentException("The port must be a number between 1 and 65535.");
            }

            return port;
        }

        public static bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        /// <summary>Returns an error message, or null when the token is acceptable.</summary>
        public static string? ValidateToken(string token)
        {
            if (token.Length < MinimumTokenLength)
            {
                return "The API token must be at least " + MinimumTokenLength.ToString(CultureInfo.InvariantCulture) + " characters long.";
            }

            foreach (char c in token)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    return "The API token cannot contain spaces or control characters.";
                }
            }

            return null;
        }

        /// <summary>
        ///     Writes only <c>Vestigo:ListenAddress</c>, <c>ListenPort</c>, and <c>ApiTokens</c> into the settings JSON and keeps
        ///     everything else. Existing tokens are kept untouched when the chosen token is already among them.
        /// </summary>
        /// <param name="existingJson">Current file contents, or null/blank for a new file. Throws <see cref="FormatException" /> when unparseable.</param>
        public static string Merge(string? existingJson, ResolvedSettings settings)
        {
            JsonObject root;
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                root = new JsonObject();
            }
            else
            {
                root = MiniJson.Parse(existingJson!) as JsonObject
                    ?? throw new FormatException("The settings file does not contain a JSON object.");
            }

            JsonObject vestigo;
            if (root.TryGet(SectionName, out object? section) && section is JsonObject existingSection)
            {
                vestigo = existingSection;
            }
            else
            {
                vestigo = new JsonObject();
                root.Set(SectionName, vestigo);
            }

            string address = ChooseAddress(settings);
            vestigo.Set("ListenAddress", address);
            vestigo.Set("ListenPort", new JsonNumber(settings.Port.ToString(CultureInfo.InvariantCulture)));

            if (settings.AuthMode == AuthToken)
            {
                bool keep = vestigo.TryGet("ApiTokens", out object? current) && current is List<object?> list &&
                            list.Exists(item => item is string text && string.Equals(text, settings.Token, StringComparison.Ordinal));
                if (!keep)
                {
                    vestigo.Set("ApiTokens", new List<object?> { settings.Token });
                }
            }
            else
            {
                vestigo.Set("ApiTokens", new List<object?>());
            }

            return MiniJson.Write(root);
        }

        private static string ChooseAddress(ResolvedSettings settings)
        {
            bool existingParsed = settings.ExistingAddress != null && IPAddress.TryParse(settings.ExistingAddress, out _);
            if (settings.BindsAny)
            {
                return existingParsed && !IsLoopbackAddress(settings.ExistingAddress) ? settings.ExistingAddress! : AnyAddress;
            }

            return existingParsed && IsLoopbackAddress(settings.ExistingAddress) ? settings.ExistingAddress! : LoopbackAddress;
        }

        private static string Normalize(string? value, string propertyName, string fallback, string first, string second)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            string trimmed = value!.Trim();
            if (string.Equals(trimmed, first, StringComparison.OrdinalIgnoreCase))
            {
                return first;
            }

            if (string.Equals(trimmed, second, StringComparison.OrdinalIgnoreCase))
            {
                return second;
            }

            throw new ArgumentException(propertyName + " must be '" + first + "' or '" + second + "'.");
        }
    }
}
