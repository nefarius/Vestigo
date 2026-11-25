using Vestigo.Input;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>Largest encoded batch that safely fits a process command line.</summary>
    public const int MaxBatchArgumentLength = 24_000;

    /// <summary>Builds helper arguments for <c>send-input</c>. The batch travels as base64 JSON in one argument.</summary>
    public static List<string> BuildSendInputArguments(InputBatch batch) =>
        InputHelperJson.BuildArguments(batch);

    public static bool TryParseSendInput(string[] args, out InputBatch batch, out string? error) =>
        InputHelperJson.TryParse(args, out batch, out error);

    private static int RunSendInput(string[] args) => InputHelperJson.Run(args);
}
