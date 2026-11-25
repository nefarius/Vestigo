using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vestigo.Vision;

/// <summary>Stdout error envelope for <c>inspect-screen</c>. A null <see cref="Reason" /> is omitted.</summary>
public sealed record InspectHelperError(
    string Error,
    string ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>
///     Source-generated JSON for <c>inspect-screen</c> results and the helper error envelope.
///     Field names, null handling, and number formatting follow <see cref="JsonSerializerDefaults.Web" />,
///     the same options as <see cref="Vestigo.Processes.SessionHelper.Json" />.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InspectCaptureResult))]
[JsonSerializable(typeof(InspectHelperError))]
public sealed partial class InspectHelperJsonContext : JsonSerializerContext;
