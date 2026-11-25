using System.Text.Json;
using System.Text.Json.Serialization;

using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>Stdout error envelope for <c>inspect-screen</c>. A null <see cref="Reason" /> is omitted.</summary>
public sealed record InspectHelperError(
    string Error,
    string ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>One JSON line sent to a waiting <c>inspect-session</c> helper.</summary>
public sealed record InspectSessionRequest(
    string Op,
    string RequestId,
    ScreenCaptureRequest? Capture = null,
    bool Uia = false,
    bool Ocr = false);

/// <summary>One JSON line written by <c>inspect-session</c>. Null fields are omitted.</summary>
public sealed record InspectSessionResponse(
    string RequestId,
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InspectCaptureResult? Result = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>Operations accepted on an <c>inspect-session</c> request line.</summary>
public static class InspectSessionOp
{
    public const string Inspect = "inspect";

    public const string Close = "close";
}

/// <summary>
///     Source-generated JSON for <c>inspect-screen</c> results, the helper error envelope, and the
///     <c>inspect-session</c> request line. Field names, null handling, and number formatting follow
///     <see cref="JsonSerializerDefaults.Web" />, the same options as <see cref="Vestigo.Processes.SessionHelper.Json" />.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InspectCaptureResult))]
[JsonSerializable(typeof(InspectHelperError))]
[JsonSerializable(typeof(InspectSessionRequest))]
[JsonSerializable(typeof(InspectSessionResponse))]
public sealed partial class InspectHelperJsonContext : JsonSerializerContext;
