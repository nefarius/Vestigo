using System.Text;
using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    public const string DesktopHostCommand = "desktop-host";

    public const string DesktopRecoverCommand = "desktop-recover";

    public const string DesktopEnumerateCommand = "desktop-enumerate";

    private static int RunDesktopHost(string[] args)
    {
        if (!TryParsePaths(args, DesktopHostCommand, "--control", "--status", out string? control, out string? status, out string? error))
        {
            return Reject(error!);
        }

        using WindowsDesktopStation station = new();
        using StreamWriter statusWriter = new(new FileStream(status!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        statusWriter.WriteLine(JsonSerializer.Serialize(DesktopCommandResult.Ready(Environment.ProcessId), Json));
        long offset = 0;
        StringBuilder pending = new();
        while (true)
        {
            string chunk = ReadNew(control!, ref offset);
            if (chunk.Length > 0)
            {
                pending.Append(chunk);
                while (TakeLine(pending, out string? line))
                {
                    DesktopCommand? command;
                    try
                    {
                        command = JsonSerializer.Deserialize<DesktopCommand>(line!, Json);
                    }
                    catch (JsonException)
                    {
                        command = null;
                    }

                    if (command is null || string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.Op))
                    {
                        continue;
                    }

                    DesktopCommandResult result = station.Execute(command);
                    statusWriter.WriteLine(JsonSerializer.Serialize(result, Json));
                    if (command.Op == DesktopHostOperations.Shutdown)
                    {
                        return 0;
                    }
                }
            }

            Thread.Sleep(30);
        }
    }

    private static int RunDesktopRecover(string[] args)
    {
        if (!TryParseOnePath(args, DesktopRecoverCommand, "--file", out string? path, out string? error))
        {
            return Reject(error!);
        }

        DesktopCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<DesktopCommand>(File.ReadAllText(path!), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Reject("The desktop recovery request could not be read.", "invalid_request");
        }

        if (command is null)
        {
            return Reject("The desktop recovery request was empty.");
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(WindowsDesktopStation.Recover(command), Json));
        return 0;
    }

    private static int RunInspectInputState(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag || args[1] != InspectInputStateCommand)
        {
            return Reject($"Usage: {Flag} {InspectInputStateCommand}");
        }

        OperationResult<InputStateReport> result = InputStateInspection.Read();
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunDesktopEnumerate(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag || args[1] != DesktopEnumerateCommand)
        {
            return Reject($"Usage: {Flag} {DesktopEnumerateCommand}");
        }

        using WindowsDesktopStation station = new();
        Console.Out.WriteLine(JsonSerializer.Serialize(station.Execute(new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Enumerate)), Json));
        return 0;
    }

    private static bool TryParsePaths(string[] args, string command, string first, string second, out string? firstValue, out string? secondValue, out string? error)
    {
        firstValue = null;
        secondValue = null;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != command)
        {
            error = $"Usage: {Flag} {command} {first} <path> {second} <path>";
            return false;
        }

        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            if (args[index] == first)
            {
                firstValue = args[index + 1];
            }
            else if (args[index] == second)
            {
                secondValue = args[index + 1];
            }
            else
            {
                error = $"Invalid option '{args[index]}'.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(firstValue) || string.IsNullOrWhiteSpace(secondValue))
        {
            error = $"Usage: {Flag} {command} {first} <path> {second} <path>";
            return false;
        }

        return true;
    }

    private static bool TryParseOnePath(string[] args, string command, string option, out string? path, out string? error)
    {
        path = null;
        error = null;
        if (!TryParsePaths(args, command, option, option, out string? first, out string? second, out error))
        {
            // TryParsePaths requires two distinct occurrences when both names match. Parse the single option directly.
            error = null;
        }

        if (args.Length == 4 && args[0] == Flag && args[1] == command && args[2] == option && !string.IsNullOrWhiteSpace(args[3]))
        {
            path = args[3];
            error = null;
            return true;
        }

        path = first ?? second;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"Usage: {Flag} {command} {option} <path>";
            return false;
        }

        return true;
    }

    private static string ReadNew(string path, ref long offset)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length)
        {
            offset = stream.Length;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        string text = reader.ReadToEnd();
        offset += Encoding.UTF8.GetByteCount(text);
        return text;
    }

    private static bool TakeLine(StringBuilder pending, out string? line)
    {
        string text = pending.ToString();
        int newline = text.IndexOf('\n');
        if (newline < 0)
        {
            line = null;
            return false;
        }

        line = text[..newline].Trim();
        pending.Clear();
        pending.Append(text[(newline + 1)..]);
        return true;
    }
}
