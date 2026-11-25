using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vestigo.Input;

/// <summary>Stdout error envelope for <c>send-input</c>. A null <see cref="Reason" /> is omitted.</summary>
public sealed record InputHelperError(
    string Error,
    string ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>
///     Source-generated JSON for the <c>send-input</c> batch, result, and error envelope.
///     Field names follow <see cref="JsonSerializerDefaults.Web" />.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InputBatch))]
[JsonSerializable(typeof(InputResult))]
[JsonSerializable(typeof(InputHelperError))]
public sealed partial class InputHelperJsonContext : JsonSerializerContext;

/// <summary>
///     Command line and JSON contract shared by <c>Vestigo.exe --session-helper send-input</c> and
///     <c>Vestigo.InputHelper.exe</c>.
/// </summary>
public static class InputHelperJson
{
    public const string Flag = "--session-helper";

    public const string Command = "send-input";

    public static List<string> BuildArguments(InputBatch batch) =>
    [
        Flag,
        Command,
        "--batch",
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(batch, InputHelperJsonContext.Default.InputBatch))
    ];

    public static bool TryParse(string[] args, out InputBatch batch, out string? error)
    {
        batch = new InputBatch([], null);
        error = null;
        if (args.Length != 4 || args[0] != Flag || args[1] != Command || args[2] != "--batch")
        {
            error = $"Usage: {Flag} {Command} --batch BASE64_JSON";
            return false;
        }

        try
        {
            InputBatch? parsed = JsonSerializer.Deserialize(Convert.FromBase64String(args[3]), InputHelperJsonContext.Default.InputBatch);
            if (parsed is null || parsed.Steps.Count == 0)
            {
                error = "The batch is empty.";
                return false;
            }

            batch = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "The batch is not valid base64 JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>Runs <c>send-input</c> and returns the process exit code. Success is 0. Rejection is 2.</summary>
    public static int Run(string[] args)
    {
        if (!TryParse(args, out InputBatch batch, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<InputResult> result = InputInjector.Execute(batch);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!, result.Reason);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, InputHelperJsonContext.Default.InputResult));
        return 0;
    }

    public static int Reject(string error, string code = "invalid_request", string? reason = null)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new InputHelperError(error, code, reason), InputHelperJsonContext.Default.InputHelperError));
        return 2;
    }
}
