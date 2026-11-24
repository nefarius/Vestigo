using System.Text;

using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class InstallerSettingsTests
{
    private const string LongToken = "0123456789abcdefghij";

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

    [Fact]
    public void SuggestedKeepToken_ReplacesInvalidReplaceInputSoKeepCanResolveWithoutExistingSettings()
    {
        string placeholder = ListenSettings.SuggestedKeepToken("short");
        Assert.NotEqual("short", placeholder);
        Assert.Null(ListenSettings.ValidateToken(placeholder));
        Assert.Equal(LongToken, ListenSettings.SuggestedKeepToken(LongToken));

        ResolvedSettings unread = ListenSettings.Resolve(
            "any",
            "token",
            "5098",
            placeholder,
            existing: null,
            tokenIsSuggestion: true);
        Assert.Equal(placeholder, unread.Token);

        ExistingSettings existing = new("0.0.0.0", 5098, [LongToken]);
        ResolvedSettings kept = ListenSettings.Resolve(
            "any",
            "token",
            "5098",
            placeholder,
            existing,
            tokenIsSuggestion: true);
        Assert.Equal(LongToken, kept.Token);
    }

    [Theory]
    [InlineData(null, null, false, false, ListenSettings.UiAuthKeep)]
    [InlineData(null, null, false, true, ListenSettings.UiAuthKeep)]
    [InlineData(ListenSettings.AuthNone, null, false, false, ListenSettings.UiAuthNone)]
    [InlineData(ListenSettings.AuthToken, null, false, true, ListenSettings.UiAuthKeep)]
    [InlineData(null, LongToken, false, false, ListenSettings.UiAuthReplace)]
    [InlineData(null, LongToken, false, true, ListenSettings.UiAuthReplace)]
    [InlineData(ListenSettings.AuthNone, LongToken, false, false, ListenSettings.UiAuthNone)]
    [InlineData(null, LongToken, true, false, ListenSettings.UiAuthKeep)]
    public void ResolveProtectedUiAuthMode_ChoosesKeepReplaceOrNone(
        string? authMode,
        string? token,
        bool tokenIsSuggestion,
        bool isUpgrade,
        string expected)
    {
        Assert.Equal(
            expected,
            ListenSettings.ResolveProtectedUiAuthMode(authMode, token, tokenIsSuggestion, isUpgrade));
    }

    [Fact]
    public void MapProtectedUiAuth_MapsKeepReplaceAndNone()
    {
        ProtectedUiAuthChoice keep = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthKeep);
        Assert.Equal(ListenSettings.AuthToken, keep.AuthMode);
        Assert.True(keep.TokenIsSuggestion);
        Assert.False(keep.RequireTokenValidation);

        ProtectedUiAuthChoice replace = ListenSettings.MapProtectedUiAuth("REPLACE");
        Assert.Equal(ListenSettings.AuthToken, replace.AuthMode);
        Assert.False(replace.TokenIsSuggestion);
        Assert.True(replace.RequireTokenValidation);

        ProtectedUiAuthChoice none = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthNone);
        Assert.Equal(ListenSettings.AuthNone, none.AuthMode);
        Assert.False(none.TokenIsSuggestion);
        Assert.False(none.RequireTokenValidation);

        ProtectedUiAuthChoice blank = ListenSettings.MapProtectedUiAuth(null);
        Assert.Equal(ListenSettings.AuthToken, blank.AuthMode);
        Assert.True(blank.TokenIsSuggestion);
        Assert.Throws<ArgumentException>(() => ListenSettings.MapProtectedUiAuth("rotate"));
    }

    [Theory]
    [InlineData(null, false, ListenSettings.UiListenKeep)]
    [InlineData("any", false, ListenSettings.UiListenKeep)]
    [InlineData("any", true, ListenSettings.UiListenAny)]
    [InlineData("LOCALHOST", true, ListenSettings.UiListenLocalhost)]
    public void ResolveProtectedUiListenMode_KeepsUnlessTheAddressWasExplicit(string? listenMode, bool explicitMode, string expected)
    {
        Assert.Equal(expected, ListenSettings.ResolveProtectedUiListenMode(listenMode, explicitMode));
    }

    [Fact]
    public void MapProtectedUiListen_MapsKeepAnyAndLocalhost()
    {
        ProtectedUiListenChoice keep = ListenSettings.MapProtectedUiListen(null);
        Assert.Equal(string.Empty, keep.ListenMode);
        Assert.False(keep.IsExplicit);

        ProtectedUiListenChoice any = ListenSettings.MapProtectedUiListen("ANY");
        Assert.Equal(ListenSettings.ListenAny, any.ListenMode);
        Assert.True(any.IsExplicit);

        ProtectedUiListenChoice local = ListenSettings.MapProtectedUiListen(ListenSettings.UiListenLocalhost);
        Assert.Equal(ListenSettings.ListenLocalhost, local.ListenMode);
        Assert.True(local.IsExplicit);
        Assert.Throws<ArgumentException>(() => ListenSettings.MapProtectedUiListen("lan"));
    }

    [Fact]
    public void ProtectedUiAuth_KeepPreservesExistingTokenReplaceOverwritesAndNoneClears()
    {
        ExistingSettings existing = new("0.0.0.0", 5098, [LongToken]);
        string generated = ListenSettings.GenerateToken();

        ProtectedUiAuthChoice keep = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthKeep);
        ResolvedSettings kept = ListenSettings.Resolve("any", keep.AuthMode, "5098", generated, existing, keep.TokenIsSuggestion);
        Assert.Equal(LongToken, kept.Token);

        ProtectedUiAuthChoice replace = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthReplace);
        ResolvedSettings replaced = ListenSettings.Resolve(
            "any",
            replace.AuthMode,
            "5098",
            generated,
            existing,
            replace.TokenIsSuggestion);
        Assert.Equal(generated, replaced.Token);

        ProtectedUiAuthChoice keepAgain = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthKeep);
        ResolvedSettings keptAgain = ListenSettings.Resolve(
            "any",
            keepAgain.AuthMode,
            "5098",
            generated,
            existing,
            keepAgain.TokenIsSuggestion);
        Assert.Equal(LongToken, keptAgain.Token);

        ProtectedUiAuthChoice none = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthNone);
        ResolvedSettings cleared = ListenSettings.Resolve("any", none.AuthMode, "5098", generated, existing, none.TokenIsSuggestion);
        Assert.Equal("", cleared.Token);
        Assert.Equal(ListenSettings.AuthNone, cleared.AuthMode);
    }

    [Fact]
    public void ProtectedUiAuth_KeepGeneratesWhenExistingHasNoToken()
    {
        ExistingSettings existing = new("0.0.0.0", 5098, []);
        string generated = ListenSettings.GenerateToken();
        ProtectedUiAuthChoice keep = ListenSettings.MapProtectedUiAuth(ListenSettings.UiAuthKeep);

        ResolvedSettings resolved = ListenSettings.Resolve("any", keep.AuthMode, "5098", generated, existing, keep.TokenIsSuggestion);

        Assert.Equal(ListenSettings.AuthToken, resolved.AuthMode);
        Assert.Equal(generated, resolved.Token);
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
    public void Merge_ReplaceApiTokensRewritesListEvenWhenTokenAlreadyPresent()
    {
        const string existing = """{ "Vestigo": { "ListenAddress": "0.0.0.0", "ListenPort": 5098, "ApiTokens": ["other-token-value", "0123456789abcdefghij"] } }""";
        ExistingSettings parsed = ListenSettings.ReadExisting(existing)!;
        ResolvedSettings keep = ListenSettings.Resolve("any", "token", "5098", LongToken, parsed);
        ResolvedSettings replace = ListenSettings.Resolve("any", "token", "5098", LongToken, parsed, replaceApiTokens: true);

        Assert.Equal(["other-token-value", LongToken], ListenSettings.ReadExisting(ListenSettings.Merge(existing, keep))!.Tokens);
        Assert.Equal([LongToken], ListenSettings.ReadExisting(ListenSettings.Merge(existing, replace))!.Tokens);
    }

    [Fact]
    public void ReadExisting_MatchesVestigoSectionCaseInsensitively()
    {
        const string json = """
            { "vestigo": {
                "listenaddress": "0.0.0.0",
                "listenport": 6000,
                "apitokens": ["0123456789abcdefghij"],
                "volumeroots": "FixedVolumes",
                "modules": { "Devices": { "Enabled": true } }
            } }
            """;

        ExistingSettings existing = Assert.IsType<ExistingSettings>(ListenSettings.ReadExisting(json));
        Assert.Equal("0.0.0.0", existing.Address);
        Assert.Equal(6000, existing.Port);
        Assert.Equal([LongToken], existing.Tokens);
        Assert.Equal("FixedVolumes", RootSettings.ReadExisting(json)!.VolumeRoots);
        Assert.True(ModuleSelection.ReadExisting(json)["Devices"]);
    }

    [Fact]
    public void Merge_MatchesListenKeysCaseInsensitively_WithoutDuplicates()
    {
        const string existing = """{ "vestigo": { "listenaddress": "127.0.0.1", "listenport": 5098, "apitokens": ["0123456789abcdefghij"] } }""";
        ExistingSettings parsed = ListenSettings.ReadExisting(existing)!;
        ResolvedSettings settings = ListenSettings.Resolve("any", "token", "6000", "replacement-token-xx", parsed);

        string merged = ListenSettings.Merge(existing, settings);

        Assert.Contains("\"vestigo\"", merged);
        Assert.Contains("\"listenaddress\": \"0.0.0.0\"", merged);
        Assert.Contains("\"listenport\": 6000", merged);
        Assert.Contains("\"apitokens\"", merged);
        Assert.DoesNotContain("\"Vestigo\"", merged);
        Assert.DoesNotContain("\"ListenAddress\"", merged);
        Assert.DoesNotContain("\"ListenPort\"", merged);
        Assert.DoesNotContain("\"ApiTokens\"", merged);
        ExistingSettings read = ListenSettings.ReadExisting(merged)!;
        Assert.Equal("0.0.0.0", read.Address);
        Assert.Equal(6000, read.Port);
        Assert.Equal(["replacement-token-xx"], read.Tokens);
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
    public void SettingsJson_RoundTripsEscapesAndUnicode()
    {
        const string text = """{ "a": "line\nbreak \"quoted\" \u00e4 \\ /", "b": [true, false, null, -1.25] }""";

        System.Text.Json.Nodes.JsonNode? parsed = SettingsJson.Parse(text);
        string written = SettingsJson.Write(parsed);

        using System.Text.Json.JsonDocument original = System.Text.Json.JsonDocument.Parse(text);
        using System.Text.Json.JsonDocument roundTrip = System.Text.Json.JsonDocument.Parse(written);
        Assert.Equal(original.RootElement.GetProperty("a").GetString(), roundTrip.RootElement.GetProperty("a").GetString());
        Assert.Equal(4, roundTrip.RootElement.GetProperty("b").GetArrayLength());
    }

    [Fact]
    public void Merge_PreservesRawNumbersAndPropertyOrder()
    {
        const string existing = """
            {"Before": [1e3, -0, 1.2300, 123456789012345678901234567890, 1e400],
             "Vestigo": {"Keep": {"Nested": 1.5E+03}}, "After": null}
            """;

        string merged = ListenSettings.Merge(existing, ListenSettings.Resolve("localhost", "none", "5098", null, null));

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(merged);
        Assert.Equal(["Before", "Vestigo", "After"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["1e3", "-0", "1.2300", "123456789012345678901234567890", "1e400"],
            document.RootElement.GetProperty("Before").EnumerateArray().Select(n => n.GetRawText()));
        Assert.Equal("1.5E+03", document.RootElement.GetProperty("Vestigo").GetProperty("Keep").GetProperty("Nested").GetRawText());
    }

    [Fact]
    public void SettingsJson_ExactDuplicatesUseLastValueAtFirstPosition()
    {
        const string text = """{"A":{"X":1,"Y":2,"X":3},"B":0,"A":{"P":4,"Q":5,"P":6},"a":7}""";

        string written = SettingsJson.Write(SettingsJson.Parse(text));

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(written);
        Assert.Equal(["A", "B", "a"], document.RootElement.EnumerateObject().Select(p => p.Name));
        System.Text.Json.JsonElement nested = document.RootElement.GetProperty("A");
        Assert.Equal(["P", "Q"], nested.EnumerateObject().Select(p => p.Name));
        Assert.Equal(6, nested.GetProperty("P").GetInt32());
        Assert.Equal(7, document.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public void SettingsJson_AcceptsBomCommentsAndTrailingCommas()
    {
        string json = "\uFEFF/* settings */ {\"Vestigo\": {\"ListenPort\": 6000,}, \"Array\": [true,],} // end";

        Assert.Equal(6000, ListenSettings.ReadExisting(json)!.Port);
        string written = SettingsJson.Write(SettingsJson.Parse(json));
        Assert.DoesNotContain('\uFEFF', written);
        Assert.DoesNotContain('\r', written);
        Assert.EndsWith("\n", written);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(written);
        Assert.True(document.RootElement.GetProperty("Array")[0].GetBoolean());
    }

    [Theory]
    [InlineData("{\"x\":01}")]
    [InlineData("{\"x\":1.}")]
    [InlineData("{\"x\":1e}")]
    [InlineData("{\"x\":\"raw\nnewline\"}")]
    [InlineData("{\"x\":1,,}")]
    [InlineData("{} true")]
    [InlineData("/* unterminated")]
    [InlineData("{\"\\uDC00\":1}")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{\"x\":\"\\uDC00\"}")]
    [InlineData("{\"x\":\"\\uD800\"}")]
    [InlineData("{\"x\":[\"\\uD800\"]}")]
    public void SettingsJson_RejectsMalformedJson(string json)
    {
        Assert.Throws<FormatException>(() => SettingsJson.Parse(json));
        Assert.Null(ListenSettings.ReadExisting(json));
        Assert.Null(RootSettings.ReadExisting(json));
        Assert.Empty(ModuleSelection.ReadExisting(json));
        Assert.Throws<FormatException>(() => ListenSettings.Merge(json,
            ListenSettings.Resolve("localhost", "none", "5098", null, null)));
    }

    [Fact]
    public void SettingsJson_Enforces64ContainerDepthLimit()
    {
        string supported = new string('[', 64) + "0" + new string(']', 64);
        Assert.NotNull(SettingsJson.Parse(supported));
        Assert.Throws<FormatException>(() => SettingsJson.Parse("[" + supported + "]"));
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
