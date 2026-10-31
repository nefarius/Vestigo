using System.Text;

using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class InstallerSettingsTests
{
    private const string LongToken = "0123456789abcdefghij";

    [Theory]
    [InlineData(ListenSettings.ListenLocalhost, ListenSettings.AuthNone, 1)]
    [InlineData(ListenSettings.ListenLocalhost, ListenSettings.AuthToken, 2)]
    [InlineData(ListenSettings.ListenAny, ListenSettings.AuthNone, 3)]
    [InlineData(ListenSettings.ListenAny, ListenSettings.AuthToken, 4)]
    public void UiChoice_RoundTripsAllFourModes(string listen, string auth, int choice)
    {
        Assert.Equal(choice, ListenSettings.ToUiChoice(listen, auth));

        ListenSettings.FromUiChoice(choice, out string mappedListen, out string mappedAuth);

        Assert.Equal(listen, mappedListen);
        Assert.Equal(auth, mappedAuth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void UiChoice_RejectsOutOfRange(int choice)
    {
        Assert.Throws<ArgumentException>(() => ListenSettings.FromUiChoice(choice, out _, out _));
    }

    [Fact]
    public void GenerateToken_IsUnpaddedBase64UrlOf32Bytes()
    {
        string first = ListenSettings.GenerateToken();
        string second = ListenSettings.GenerateToken();

        Assert.Equal(43, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(first, second);
        Assert.Equal(32, Convert.FromBase64String(first.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Null(ListenSettings.ValidateToken(first));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", false)]
    [InlineData("::1", "localhost", false)]
    [InlineData("0.0.0.0", "any", false)]
    [InlineData("::", "any", false)]
    [InlineData("192.168.1.5", "any", true)]
    [InlineData("garbage", "localhost", true)]
    public void MapExisting_MapsAddressAndTokenPresence(string address, string expectedListen, bool withToken)
    {
        string[] tokens = withToken ? [LongToken] : [];

        ListenSettings.MapExisting(new ExistingSettings(address, 1234, tokens), out string listen, out string auth);

        Assert.Equal(expectedListen, listen);
        Assert.Equal(withToken ? ListenSettings.AuthToken : ListenSettings.AuthNone, auth);
    }

    [Fact]
    public void ReadExisting_ParsesAddressPortAndTokens()
    {
        ExistingSettings? existing = ListenSettings.ReadExisting(
            """{ "Vestigo": { "ListenAddress": "0.0.0.0", "ListenPort": 6000, "ApiTokens": ["a-token", " ", 5] } }""");

        Assert.NotNull(existing);
        Assert.Equal("0.0.0.0", existing.Address);
        Assert.Equal(6000, existing.Port);
        Assert.Equal(["a-token"], existing.Tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "Other": 1 }""")]
    public void ReadExisting_ReturnsNullWhenUnusable(string? json)
    {
        Assert.Null(ListenSettings.ReadExisting(json));
    }

    [Fact]
    public void Resolve_UpgradeWithoutReadableSettingsDefaultsToLocalhostWithoutToken()
    {
        ResolvedSettings resolved = ListenSettings.Resolve(null, null, null, null, null, isUpgrade: true);

        Assert.Equal(ListenSettings.ListenLocalhost, resolved.ListenMode);
        Assert.Equal(ListenSettings.AuthNone, resolved.AuthMode);
        Assert.Equal(string.Empty, resolved.Token);
    }
    [Fact]
    public void Resolve_DefaultsToAnyWithGeneratedToken()
    {
        ResolvedSettings resolved = ListenSettings.Resolve(null, null, null, null, null);

        Assert.Equal(ListenSettings.ListenAny, resolved.ListenMode);
        Assert.Equal(ListenSettings.AuthToken, resolved.AuthMode);
        Assert.Equal(5098, resolved.Port);
        Assert.Equal(43, resolved.Token.Length);
        Assert.Equal(4, resolved.UiChoice);
    }

    [Fact]
    public void Resolve_NoTokenModeClearsToken()
    {
        ResolvedSettings resolved = ListenSettings.Resolve("LOCALHOST", "None", "6001", LongToken, null);

        Assert.Equal(ListenSettings.ListenLocalhost, resolved.ListenMode);
        Assert.Equal(ListenSettings.AuthNone, resolved.AuthMode);
        Assert.Equal(6001, resolved.Port);
        Assert.Equal("", resolved.Token);
    }

    [Fact]
    public void Resolve_UsesExistingSettingsWhenNothingIsSpecified()
    {
        ExistingSettings existing = new("127.0.0.1", 7000, [LongToken]);

        ResolvedSettings resolved = ListenSettings.Resolve(null, null, null, null, existing);

        Assert.Equal(ListenSettings.ListenLocalhost, resolved.ListenMode);
        Assert.Equal(ListenSettings.AuthToken, resolved.AuthMode);
        Assert.Equal(7000, resolved.Port);
        Assert.Equal(LongToken, resolved.Token);
        Assert.Equal(2, resolved.UiChoice);
    }

    [Fact]
    public void Resolve_KeepsExistingTokenWhenOnlyASuggestionWasAccepted()
    {
        ExistingSettings existing = new("0.0.0.0", 5098, [LongToken]);
        string suggestion = ListenSettings.GenerateToken();

        ResolvedSettings unchanged = ListenSettings.Resolve("any", "token", "5098", suggestion, existing, tokenIsSuggestion: true);
        ResolvedSettings edited = ListenSettings.Resolve("any", "token", "5098", suggestion, existing);

        Assert.Equal(LongToken, unchanged.Token);
        Assert.Equal(suggestion, edited.Token);
    }

    [Theory]
    [InlineData("sideways", "token", "5098", LongToken)]
    [InlineData("any", "maybe", "5098", LongToken)]
    [InlineData("any", "token", "0", LongToken)]
    [InlineData("any", "token", "65536", LongToken)]
    [InlineData("any", "token", "abc", LongToken)]
    [InlineData("any", "token", "5098", "short")]
    [InlineData("any", "token", "5098", "has space in the token")]
    public void Resolve_RejectsInvalidValues(string listen, string auth, string port, string token)
    {
        Assert.Throws<ArgumentException>(() => ListenSettings.Resolve(listen, auth, port, token, null));
    }

    [Fact]
    public void Merge_CreatesFileForTokenMode()
    {
        ResolvedSettings settings = ListenSettings.Resolve("any", "token", "5099", LongToken, null);

        string json = ListenSettings.Merge(null, settings);

        ExistingSettings? roundTrip = ListenSettings.ReadExisting(json);
        Assert.NotNull(roundTrip);
        Assert.Equal("0.0.0.0", roundTrip.Address);
        Assert.Equal(5099, roundTrip.Port);
        Assert.Equal([LongToken], roundTrip.Tokens);
    }

    [Fact]
    public void Merge_PreservesOtherKeysAndOnlyTouchesListenKeys()
    {
        const string existing = """
            // machine settings
            {
              "Vestigo": {
                "ListenAddress": "127.0.0.1",
                "ListenPort": 5098,
                "ApiTokens": [],
                "AllowedInputRoots": ["D:\\traces", "E:\\more"],
                "MaxInputBytes": 4294967296,
                "MaxCaptureDuration": "01:00:00",
                "SymbolServer": null,
                "Custom": { "Flag": true, "Ratio": 1.5e3 },
              },
              /* logging */
              "Serilog": { "MinimumLevel": { "Default": "Warning" } },
              "AllowedHosts": "*"
            }
            """;
        ResolvedSettings settings = ListenSettings.Resolve("any", "token", "6000", LongToken, null);

        string merged = ListenSettings.Merge(existing, settings);

        Assert.Equal(
            Flatten(existing, ["Vestigo:ListenAddress", "Vestigo:ListenPort", "Vestigo:ApiTokens"]),
            Flatten(merged, ["Vestigo:ListenAddress", "Vestigo:ListenPort", "Vestigo:ApiTokens"]));
        ExistingSettings? read = ListenSettings.ReadExisting(merged);
        Assert.NotNull(read);
        Assert.Equal("0.0.0.0", read.Address);
        Assert.Equal(6000, read.Port);
        Assert.Equal([LongToken], read.Tokens);
        Assert.Contains("1.5e3", merged);
        Assert.Contains("D:\\\\traces", merged);
    }

    [Fact]
    public void Merge_KeepsExistingAddressAndTokenListWhenStillMatching()
    {
        const string existing = """{ "Vestigo": { "ListenAddress": "192.168.1.5", "ListenPort": 5098, "ApiTokens": ["other-token-value", "0123456789abcdefghij"] } }""";
        ExistingSettings parsed = ListenSettings.ReadExisting(existing)!;
        ResolvedSettings settings = ListenSettings.Resolve("any", "token", "5098", LongToken, parsed);

        string merged = ListenSettings.Merge(existing, settings);

        ExistingSettings read = ListenSettings.ReadExisting(merged)!;
        Assert.Equal("192.168.1.5", read.Address);
        Assert.Equal(["other-token-value", LongToken], read.Tokens);
    }

    [Fact]
    public void Merge_NoTokenModeEmptiesTokensAndLocalhostUsesLoopback()
    {
        const string existing = """{ "Vestigo": { "ListenAddress": "0.0.0.0", "ApiTokens": ["0123456789abcdefghij"] } }""";
        ResolvedSettings settings = ListenSettings.Resolve("localhost", "none", "5098", null, ListenSettings.ReadExisting(existing));

        ExistingSettings read = ListenSettings.ReadExisting(ListenSettings.Merge(existing, settings))!;

        Assert.Equal("127.0.0.1", read.Address);
        Assert.Empty(read.Tokens);
    }

    [Fact]
    public void Merge_ReplacesNonObjectSectionAndRejectsInvalidRoot()
    {
        ResolvedSettings settings = ListenSettings.Resolve("localhost", "none", "5098", null, null);

        string merged = ListenSettings.Merge("""{ "Vestigo": 3, "Keep": "me" }""", settings);

        Assert.Contains("\"Keep\": \"me\"", merged);
        Assert.NotNull(ListenSettings.ReadExisting(merged));
        Assert.Throws<FormatException>(() => ListenSettings.Merge("[1, 2]", settings));
        Assert.Throws<FormatException>(() => ListenSettings.Merge("{ broken", settings));
    }

    [Fact]
    public void Merge_OutputIsLfJsonWithoutBomOrTrailingCommas()
    {
        ResolvedSettings settings = ListenSettings.Resolve("any", "none", "5098", null, null);

        string merged = ListenSettings.Merge(null, settings);

        Assert.DoesNotContain('\uFEFF', merged);
        Assert.DoesNotContain(",\n}", merged);
        Assert.StartsWith("{\n  \"Vestigo\": {", merged);
        Assert.Equal(merged, Encoding.UTF8.GetString(new UTF8Encoding(false).GetBytes(merged)));
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(merged);
        Assert.Equal("0.0.0.0", document.RootElement.GetProperty("Vestigo").GetProperty("ListenAddress").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("Vestigo").GetProperty("ApiTokens").GetArrayLength());
    }

    [Fact]
    public void MiniJson_RoundTripsEscapesAndUnicode()
    {
        const string text = """{ "a": "line\nbreak \"quoted\" \u00e4 \\ /", "b": [true, false, null, -1.25] }""";

        object? parsed = MiniJson.Parse(text);
        string written = MiniJson.Write(parsed);

        using System.Text.Json.JsonDocument original = System.Text.Json.JsonDocument.Parse(text);
        using System.Text.Json.JsonDocument roundTrip = System.Text.Json.JsonDocument.Parse(written);
        Assert.Equal(original.RootElement.GetProperty("a").GetString(), roundTrip.RootElement.GetProperty("a").GetString());
        Assert.Equal(4, roundTrip.RootElement.GetProperty("b").GetArrayLength());
    }

    private static Dictionary<string, string> Flatten(string json, string[] excluded)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            json,
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        Dictionary<string, string> result = [];
        Visit(document.RootElement, "", result);
        foreach (string key in result.Keys.Where(k => excluded.Any(e => k == e || k.StartsWith(e + "[", StringComparison.Ordinal) || k.StartsWith(e + "#", StringComparison.Ordinal))).ToList())
        {
            result.Remove(key);
        }

        return result;
    }

    private static void Visit(System.Text.Json.JsonElement element, string path, Dictionary<string, string> result)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
            {
                Visit(property.Value, path.Length == 0 ? property.Name : path + ":" + property.Name, result);
            }

            result[path + "#object"] = "";
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            int index = 0;
            foreach (System.Text.Json.JsonElement item in element.EnumerateArray())
            {
                Visit(item, path + "[" + index++ + "]", result);
            }

            result[path + "#array"] = index.ToString();
        }
        else
        {
            result[path] = element.GetRawText();
        }
    }
}