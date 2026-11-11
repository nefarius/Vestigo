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
        if (args.Length < 2 || args[0] != Flag || args[1] != DesktopHostCommand)
        {
            return Reject($"Usage: {Flag} {DesktopHostCommand}");
        }

        using WindowsDesktopStation station = new();
        using StreamWriter statusWriter = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        using StreamReader commands = new(Console.OpenStandardInput(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        statusWriter.WriteLine(JsonSerializer.Serialize(DesktopCommandResult.Ready(Environment.ProcessId), Json));
        string? line;
        while ((line = commands.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            DesktopCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<DesktopCommand>(line, Json);
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

        return 0;
    }

    private static int RunDesktopRecover(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag || args[1] != DesktopRecoverCommand)
        {
            return Reject($"Usage: {Flag} {DesktopRecoverCommand}");
        }

        string text;
        using (StreamReader reader = new(Console.OpenStandardInput(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        {
            text = reader.ReadToEnd();
        }

        DesktopCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<DesktopCommand>(text, Json);
        }
        catch (JsonException)
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

}
