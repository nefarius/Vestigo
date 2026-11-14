using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Minisign;
using Minisign.Models;

using Nefarius.Vicius.Abstractions.Models;

namespace Vestigo.Updates;

/// <summary>Fetches and verifies the signed Buildbot update feed.</summary>
public interface IUpdateFeedClient
{
    Task<OperationResult<UpdateFeedRelease>> GetLatestAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Downloads <c>updates.json</c> and its <c>.minisig</c> sidecar, verifies the minisign signature,
///     refuses package URLs outside the Buildbot directory, allows the summary to link to the Vestigo
///     GitHub Release, and returns the newest enabled release.
/// </summary>
public sealed class UpdateFeedClient : IUpdateFeedClient
{
    public const string HttpClientName = "updates";

    // Keep this aligned with Vestigo.Build.UpdateFeed. Release tags are MinVer v* tags.
    private static readonly Regex ReleaseTagPattern = new(
        @"^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:\.(?:0|[1-9]\d*))?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new ProductVersionConverter() }
    };

    private readonly IHttpClientFactory _httpClients;
    private readonly string _publicKey;
    private readonly string _directoryUrl;

    public UpdateFeedClient(IHttpClientFactory httpClients)
        : this(httpClients, UpdateEndpoints.ManifestPublicKey, UpdateEndpoints.DirectoryUrl)
    {
    }

    internal UpdateFeedClient(IHttpClientFactory httpClients, string publicKey, string directoryUrl)
    {
        _httpClients = httpClients;
        _publicKey = publicKey;
        _directoryUrl = directoryUrl.EndsWith('/') ? directoryUrl : directoryUrl + "/";
    }

    public async Task<OperationResult<UpdateFeedRelease>> GetLatestAsync(CancellationToken cancellationToken)
    {
        byte[] manifest;
        byte[] sidecar;
        try
        {
            manifest = await DownloadAsync(UpdateEndpoints.ManifestFileName, cancellationToken).ConfigureAwait(false);
            sidecar = await DownloadAsync(UpdateEndpoints.ManifestSignatureFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Fail("feed_unavailable", "The signed update feed could not be downloaded: " + ex.Message);
        }

        if (!VerifySignature(manifest, sidecar, _publicKey, out string? verifyError))
        {
            return Fail("manifest_invalid", verifyError ?? "The update feed signature is not valid.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifest);
        }
        catch (JsonException ex)
        {
            return Fail("manifest_invalid", "The update feed is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            if (!TryRequireBuildbotResources(document.RootElement, _directoryUrl, out string? urlError))
            {
                return Fail("manifest_invalid", urlError!);
            }

            if (!document.RootElement.TryGetProperty("manifestVersion", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt64(out long manifestVersion) ||
                manifestVersion <= 0)
            {
                return Fail("manifest_invalid", "The update feed has no positive manifestVersion.");
            }

            UpdateResponse? response;
            try
            {
                response = document.Deserialize<UpdateResponse>(SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Fail("manifest_invalid", "The update feed could not be parsed: " + ex.Message);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
            {
                return Fail("manifest_invalid", "This build cannot read the update feed models: " + ex.Message);
            }

            if (response?.Releases is null || response.Releases.Count == 0)
            {
                return Fail("manifest_invalid", "The update feed contains no releases.");
            }

            UpdateRelease? newest = response.Releases
                .Where(release => release.Disabled != true)
                .OrderByDescending(release => Normalize(release.Version))
                .ThenByDescending(release => release.PublishedAt)
                .FirstOrDefault();
            if (newest is null)
            {
                return Fail("manifest_invalid", "The update feed contains no enabled releases.");
            }

            return OperationResult<UpdateFeedRelease>.Ok(new UpdateFeedRelease(
                newest.Version.ToString(),
                manifestVersion,
                newest.PublishedAt,
                newest.Summary,
                newest.DownloadUrl,
                newest.DownloadSize));
        }
    }

    private async Task<byte[]> DownloadAsync(string fileName, CancellationToken cancellationToken)
    {
        HttpClient client = _httpClients.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Get, _directoryUrl + fileName);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound &&
            fileName.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("The update feed signature sidecar is missing.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > UpdateEndpoints.MaxManifestBytes)
        {
            throw new IOException($"The update feed file '{fileName}' exceeds the {UpdateEndpoints.MaxManifestBytes} byte limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > UpdateEndpoints.MaxManifestBytes)
            {
                throw new IOException($"The update feed file '{fileName}' exceeds the {UpdateEndpoints.MaxManifestBytes} byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    internal static bool VerifySignature(byte[] manifest, byte[] sidecar, string publicKey, out string? error)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vestigo-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string manifestPath = Path.Combine(directory, UpdateEndpoints.ManifestFileName);
            File.WriteAllBytes(manifestPath, manifest);
            File.WriteAllBytes(manifestPath + ".minisig", sidecar);
            MinisignPublicKey key = Core.LoadPublicKeyFromString(publicKey);
            MinisignSignature signature = Core.LoadSignatureFromFile(manifestPath + ".minisig");
            if (!Core.ValidateHashedSignature(manifestPath, signature, key))
            {
                error = "The update feed minisign sidecar is not valid.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or FormatException)
        {
            error = "The update feed signature could not be verified: " + ex.Message;
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temporary verification files.
            }
        }
    }

    internal static bool TryRequireBuildbotResources(JsonElement root, string directoryUrl, out string? error)
    {
        try
        {
            Walk(root, directoryUrl, propertyName: null);
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Walk(JsonElement element, string directoryUrl, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, directoryUrl, property.Name);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, directoryUrl, propertyName);
                }

                break;
            case JsonValueKind.String:
                RequireFeedUrl(propertyName, element.GetString() ?? "", directoryUrl);
                break;
        }
    }

    private static void RequireFeedUrl(string? propertyName, string value, string directoryUrl)
    {
        if (IsSummary(propertyName))
        {
            foreach (string url in HttpUrls(value))
            {
                if (!IsAllowedSummaryUrl(url, directoryUrl))
                {
                    throw new InvalidOperationException(FeedUrlMessage(directoryUrl, url));
                }
            }

            return;
        }

        if (IsAbsoluteHttp(value) && !value.StartsWith(directoryUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(FeedUrlMessage(directoryUrl, value));
        }
    }

    private static bool IsAllowedSummaryUrl(string url, string directoryUrl) =>
        url.StartsWith(directoryUrl, StringComparison.Ordinal) || IsReleaseNotesUrl(url);

    private static bool IsReleaseNotesUrl(string releaseUrl)
    {
        string candidate = (releaseUrl ?? "").Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        const string pathPrefix = "/nefarius/Vestigo/releases/tag/";
        if (!uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string tag = uri.AbsolutePath[pathPrefix.Length..];
        if (tag.Contains('/') || !ReleaseTagPattern.IsMatch(tag))
        {
            return false;
        }

        return string.Equals(candidate, UpdateEndpoints.ReleaseNotesUrlPrefix + tag, StringComparison.Ordinal);
    }

    private static bool IsSummary(string? propertyName) =>
        string.Equals(propertyName, "summary", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsoluteHttp(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> HttpUrls(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            ReadOnlySpan<char> rest = text.AsSpan(index);
            if (!rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int end = index;
            while (end < text.Length && !IsUrlTerminator(text[end]))
            {
                end++;
            }

            while (end > index && text[end - 1] is '.' or ',' or ';' or ':' or '!')
            {
                end--;
            }

            if (end > index)
            {
                yield return text[index..end];
            }

            index = Math.Max(end, index + 1) - 1;
        }
    }

    private static bool IsUrlTerminator(char value) =>
        char.IsWhiteSpace(value) || value is '<' or '>' or '"' or '\'' or ')' or ']' or '`';

    private static string FeedUrlMessage(string directoryUrl, string value) =>
        $"The update feed may only reference {directoryUrl}, or a Vestigo GitHub release link in the summary. Found '{value}'.";

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        if (!Version.TryParse(trimmed, out Version? parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    internal static bool IsNewer(string latest, string installed) =>
        TryParseVersion(latest, out Version latestVersion) &&
        (!TryParseVersion(installed, out Version installedVersion) || latestVersion > installedVersion);

    private static OperationResult<UpdateFeedRelease> Fail(string code, string message) =>
        OperationResult<UpdateFeedRelease>.Fail(code, message);

    private sealed class ProductVersionConverter : JsonConverter<Version>
    {
        public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Version.Parse(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
