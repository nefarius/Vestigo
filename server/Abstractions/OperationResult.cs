using System.Text.Json.Serialization;

namespace Vestigo.Abstractions;

/// <summary>
///     Success value or a stable error code returned by a tool or service.
/// </summary>
/// <param name="Reason">Optional stable token, such as why no console user is available. Omitted when null.</param>
public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    string? ErrorCode,
    string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    public static OperationResult<T> Ok(T value) => new(true, value, null, null);

    public static OperationResult<T> Fail(string code, string error, string? reason = null) =>
        new(false, default, code, error, reason);

    /// <summary>Copies this failure, including <see cref="Reason" />, onto another result type.</summary>
    public OperationResult<TResult> FailAs<TResult>() =>
        OperationResult<TResult>.Fail(ErrorCode!, Error!, Reason);
}
