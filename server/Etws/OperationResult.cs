namespace Vestigo.Etws;

/// <summary>
///     Success value or a stable error code returned by a tool or service.
/// </summary>
public sealed record OperationResult<T>(bool Success, T? Value, string? ErrorCode, string? Error)
{
    public static OperationResult<T> Ok(T value) => new(true, value, null, null);

    public static OperationResult<T> Fail(string code, string error) => new(false, default, code, error);
}
