using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Minisign;
using Minisign.Models;

using Nefarius.Vicius.Abstractions.Converters;
using Nefarius.Vicius.Abstractions.Models;

namespace Vestigo.Build;

/// <summary>
/// Buildbot update-feed addresses and the pinned Vicius release consumed by the installer.
/// </summary>
public static class UpdateFeed
{
    public const string DirectoryUrl = "https://buildbot.nefarius.at/builds/Vestigo/latest/";
    public const string ReleaseNotesUrlPrefix = "https://github.com/nefarius/Vestigo/releases/tag/";
    public const string ManifestFileName = "updates.json";
    public const string PackageFileNamePrefix = "Vestigo-v";
    public const string PackageFileNamePattern = PackageFileNamePrefix + "*.msi";
    public const string CurrentPackagePointerFileName = "current-msi.txt";
    public const string ManifestUrl = DirectoryUrl + ManifestFileName;
    public const string PublisherSubject = "Nefarius Software Solutions e.U.";
    public const string ManifestPublicKey = "RWTZDJM+Ify8UukJiWfvLcLvjvrVlHcXY/A9cgbOIStr+c19FIlY8x4B";
    public const string RegistryKey = @"SOFTWARE\Nefarius Software Solutions\Vestigo";
    public const string RegistryValue = "Version";
    public const string LaunchArguments = "/passive /norestart";
    public const string UpdaterVersion = "1.20.1";
    public const string UpdaterArchiveSha256 = "264669ec480264f36bb425dd81d51e6402f61d68d9177522f914aa9fcdfca806";
    public const string UpdaterArchiveUrl = "https://github.com/nefarius/vicius/releases/download/v1.20.1/vicius-x64.zip";
    public const string UpdaterArchiveExecutable = "example_Demo_Updater.exe";
    public const string UpdaterExecutable = "Nefarius_Vestigo_Updater.exe";
    public const string UpdaterConfiguration = "Nefarius_Vestigo_Updater.json";

    static readonly Regex ProductVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\.(0|[1-9]\d*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Keep this aligned with Vestigo.Updates.UpdateFeedClient. Release tags are MinVer v* tags,
    // including an optional numeric revision and a prerelease suffix.
    static readonly Regex ReleaseTagPattern = new(
        @"^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:\.(?:0|[1-9]\d*))?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters =
        {
            new DateTimeOffsetConverter(),
            new JsonStringEnumConverter(),
            new ProductVersionConverter()
        }
    };

    public static byte[] CreateManifest(UpdateManifestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.ManifestVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "manifestVersion must be a positive integer.");
        }

        if (spec.DownloadSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "The MSI size must be positive.");
        }

        string productVersion = RequireProductVersion(spec.ProductVersion);
        string checksum = RequireSha256(spec.Sha256);
        string summary = string.IsNullOrWhiteSpace(spec.Summary)
            ? DefaultSummary(productVersion, RequireReleaseNotesUrl(spec.ReleaseUrl))
            : spec.Summary.Trim();

        UpdateRelease release = new()
        {
            Name = "Vestigo " + productVersion,
            Version = Version.Parse(productVersion),
            Summary = summary,
            PublishedAt = spec.PublishedAt.ToUniversalTime(),
            DownloadUrl = PackageUrl(productVersion),
            DownloadSize = spec.DownloadSize,
            LaunchArguments = LaunchArguments,
            RunAsAdmin = true,
            Checksum = new ChecksumParameters
            {
                Checksum = checksum,
                ChecksumAlg = ChecksumAlgorithm.SHA256
            },
            SignaturePolicy = SignatureComparisonPolicy.Strict,
            SignatureStrategy = SignatureVerificationStrategy.FromConfiguration,
            Signature = new SignatureConfig
            {
                SubjectName = PublisherSubject
            },
            ExitCode = new ExitCodeCheck
            {
                Messages = new Dictionary<string, ExitCodeMessage>
                {
                    ["3010"] = new()
                    {
                        IsSuccess = true,
                        Message = "Installation succeeded. Restart Windows to complete the update."
                    },
                    ["1641"] = new()
                    {
                        IsSuccess = true,
                        Message = "Installation succeeded and Windows is restarting."
                    },
                    ["1602"] = new()
                    {
                        Message = "The installation was cancelled. No update was applied."
                    }
                }
            }
        };
        release.ExitCode.SuccessCodes.Add(0);
        release.ExitCode.SuccessCodes.Add(3010);
        release.ExitCode.SuccessCodes.Add(1641);

        UpdateResponse response = new()
        {
            Shared = new SharedConfig
            {
                WindowTitle = "Vestigo Updater",
                ProductName = "Vestigo",
                SignatureVerificationMode = SignatureVerificationMode.Required,
                SignaturePolicy = SignatureComparisonPolicy.Strict,
                SignatureStrategy = SignatureVerificationStrategy.FromConfiguration,
                SignatureConfig = new SignatureConfig
                {
                    SubjectName = PublisherSubject
                }
            },
            Releases = { release }
        };

        JsonObject root = JsonSerializer.SerializeToNode(response, SerializerOptions) as JsonObject
            ?? throw new InvalidOperationException("The update manifest root must be a JSON object.");
        root.Insert(0, "manifestVersion", spec.ManifestVersion);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
        using JsonDocument document = JsonDocument.Parse(bytes);
        RequireBuildbotResources(document.RootElement);
        if (document.RootElement.GetProperty("manifestVersion").GetInt64() != spec.ManifestVersion)
        {
            throw new InvalidOperationException("manifestVersion was not written as a JSON integer.");
        }

        return bytes;
    }

    public static void SignManifest(string manifestPath, string privateKeyPath, string password)
    {
        byte[] before = File.ReadAllBytes(manifestPath);
        MinisignPrivateKey key = Core.LoadPrivateKeyFromFile(privateKeyPath, password);
        string signaturePath = Core.SignHashed(manifestPath, key);
        string expected = manifestPath + ".minisig";
        if (!string.Equals(Path.GetFullPath(signaturePath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected minisign sidecar '{expected}' but signing wrote '{signaturePath}'.");
        }

        byte[] after = File.ReadAllBytes(manifestPath);
        if (!before.AsSpan().SequenceEqual(after))
        {
            throw new InvalidOperationException("Signing changed the manifest bytes.");
        }
    }

    public static void VerifyManifest(string manifestPath, string publicKey)
    {
        MinisignPublicKey key = Core.LoadPublicKeyFromString(publicKey);
        MinisignSignature signature = Core.LoadSignatureFromFile(manifestPath + ".minisig");
        if (!Core.ValidateHashedSignature(manifestPath, signature, key))
        {
            throw new InvalidOperationException("The update manifest minisign sidecar is not valid.");
        }
    }

    public static void RequireBuildbotResources(JsonElement root) => Walk(root, propertyName: "");

    public static string RequireReleaseNotesUrl(string releaseUrl)
    {
        if (!TryCanonicalReleaseNotesUrl(releaseUrl, out string canonical))
        {
            throw new ArgumentException(
                $"The release URL must be {ReleaseNotesUrlPrefix}<v-tag>.",
                nameof(releaseUrl));
        }

        return canonical;
    }

    public static string DefaultSummary(string productVersion, string releaseUrl)
    {
        string version = RequireProductVersion(productVersion);
        string notesUrl = RequireReleaseNotesUrl(releaseUrl);
        return $"# Vestigo {version}{Environment.NewLine}{Environment.NewLine}" +
               $"Signed release of the Vestigo MCP server.{Environment.NewLine}{Environment.NewLine}" +
               $"[Release notes]({notesUrl})";
    }

    static void Walk(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, property.Name);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, propertyName);
                }

                break;
            case JsonValueKind.String:
                RequireManifestUrl(propertyName, element.GetString() ?? "");
                break;
        }
    }

    static void RequireManifestUrl(string propertyName, string value)
    {
        if (IsSummary(propertyName))
        {
            foreach (string url in HttpUrls(value))
            {
                if (!IsAllowedSummaryUrl(url))
                {
                    throw new InvalidOperationException(ManifestUrlMessage(url));
                }
            }

            return;
        }

        if (IsAbsoluteHttp(value) && !value.StartsWith(DirectoryUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ManifestUrlMessage(value));
        }
    }

    static bool IsAllowedSummaryUrl(string url) =>
        url.StartsWith(DirectoryUrl, StringComparison.Ordinal) ||
        TryCanonicalReleaseNotesUrl(url, out _);

    static bool TryCanonicalReleaseNotesUrl(string releaseUrl, out string canonical)
    {
        canonical = "";
        string candidate = releaseUrl?.Trim() ?? "";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri) ||
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

        canonical = ReleaseNotesUrlPrefix + tag;
        return string.Equals(candidate, canonical, StringComparison.Ordinal);
    }

    static bool IsSummary(string propertyName) =>
        string.Equals(propertyName, "summary", StringComparison.OrdinalIgnoreCase);

    static bool IsAbsoluteHttp(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> HttpUrls(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (!StartsWithHttp(text, index))
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

    static bool StartsWithHttp(string text, int index)
    {
        ReadOnlySpan<char> rest = text.AsSpan(index);
        return rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsUrlTerminator(char value) =>
        char.IsWhiteSpace(value) || value is '<' or '>' or '"' or '\'' or ')' or ']' or '`';

    static string ManifestUrlMessage(string value) =>
        $"The update manifest may only reference {DirectoryUrl}, or a Vestigo GitHub release link in the summary. Found '{value}'.";

    public static string PackageFileName(string productVersion)
        => PackageFileNamePrefix + RequireProductVersion(productVersion) + ".msi";

    public static string PackageUrl(string productVersion)
        => DirectoryUrl + PackageFileName(productVersion);

    public static string RequireProductVersion(string productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion) || !ProductVersionPattern.IsMatch(productVersion))
        {
            throw new ArgumentException(
                "The MSI product version must be a numeric major.minor.patch or major.minor.patch.revision value.",
                nameof(productVersion));
        }

        return productVersion;
    }

    public static string RequireSha256(string sha256)
    {
        string normalized = (sha256 ?? "").Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The MSI checksum must be a SHA-256 hex digest.", nameof(sha256));
        }

        return normalized;
    }
}

public sealed class UpdateManifestSpec
{
    public required string ProductVersion { get; init; }

    public required long ManifestVersion { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required long DownloadSize { get; init; }

    public required string Sha256 { get; init; }

    public string Summary { get; init; }

    /// <summary>
    ///     Canonical GitHub Release URL, <c>https://github.com/nefarius/Vestigo/releases/tag/&lt;tag&gt;</c>.
    ///     Required when <see cref="Summary" /> is empty. The page can be published after the manifest is signed.
    /// </summary>
    public string ReleaseUrl { get; init; }
}

sealed class ProductVersionConverter : JsonConverter<Version>
{
    public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Version.Parse(reader.GetString() ?? "");
    }

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
