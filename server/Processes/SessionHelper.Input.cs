using System.Text;
using System.Text.Json;

using Vestigo.Input;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>Largest encoded batch that safely fits a process command line.</summary>
    public const int MaxBatchArgumentLength = 24_000;

    /// <summary>Builds helper arguments for <c>send-input</c>. The batch travels as base64 JSON in one argument.</summary>
    public static List<string> BuildSendInputArguments(InputBatch batch) =>
    [
        Flag,
        SendInputCommand,
        "--batch",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, Json)))
    ];

    public static bool TryParseSendInput(string[] args, out InputBatch batch, out string? error)
    {
        batch = new InputBatch([], null);
        error = null;
        if (args.Length != 4 || args[0] != Flag || args[1] != SendInputCommand || args[2] != "--batch")
        {
            error = $"Usage: {Flag} {SendInputCommand} --batch BASE64_JSON";
            return false;
        }

        try
        {
            InputBatch? parsed = JsonSerializer.Deserialize<InputBatch>(Convert.FromBase64String(args[3]), Json);
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

    private static int RunSendInput(string[] args)
    {
        if (!TryParseSendInput(args, out InputBatch batch, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<InputResult> result = InputInjector.Execute(batch);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!, result.Reason);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }
}
