using System.Globalization;
using System.Text.Json;

using Vestigo.Audio;

namespace Vestigo.Processes;

public static partial class SessionHelper
{

    /// <summary>Builds helper arguments (after the executable) for an audio endpoint listing.</summary>
    public static List<string> BuildListAudioArguments(AudioSelector selector)
    {
        List<string> arguments =
        [
            Flag,
            ListAudioCommand,
            "--flow",
            selector.Flow,
            "--state",
            selector.State,
            "--include-sessions",
            selector.IncludeSessions ? "true" : "false",
            "--meter-ms",
            selector.MeterSampleMilliseconds.ToString(CultureInfo.InvariantCulture)
        ];
        if (!string.IsNullOrEmpty(selector.Id))
        {
            arguments.Add("--id");
            arguments.Add(selector.Id);
        }

        return arguments;
    }

    /// <summary>Parses helper arguments (starting at the flag) into an audio listing selector.</summary>
    public static bool TryParseListAudio(string[] args, out AudioSelector selector, out string? error)
    {
        selector = AudioSelector.Defaults;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ListAudioCommand)
        {
            error = $"Usage: {Flag} {ListAudioCommand} [--flow all|render|capture] [--state active|all] [--id ID] [--include-sessions true|false] [--meter-ms N]";
            return false;
        }

        string flow = AudioSelector.FlowAll;
        string state = AudioSelector.StateActive;
        string? id = null;
        bool includeSessions = true;
        int meterMs = AudioOptions.DefaultMeterSampleMilliseconds;
        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            string value = args[index + 1];
            switch (args[index])
            {
                case "--flow" when AudioSelector.TryParseFlow(value, out flow, out error):
                    break;
                case "--state" when AudioSelector.TryParseState(value, out state, out error):
                    break;
                case "--id" when value.Length > 0:
                    id = value;
                    break;
                case "--include-sessions" when TryParseFlag(value, out includeSessions):
                    break;
                case "--meter-ms" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= AudioOptions.MaxMeterSampleMilliseconds:
                    meterMs = parsed;
                    break;
                default:
                    error = error ?? $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        selector = new AudioSelector(flow, state, id, includeSessions, meterMs);
        return true;
    }

    /// <summary>Builds helper arguments (after the executable) for setting the default audio endpoint.</summary>
    public static List<string> BuildSetDefaultAudioArguments(string id, string role) =>
        [Flag, SetDefaultAudioCommand, "--id", id, "--role", role];

    /// <summary>Parses helper arguments (starting at the flag) for setting the default audio endpoint.</summary>
    public static bool TryParseSetDefaultAudio(string[] args, out string id, out string role, out string? error)
    {
        id = "";
        role = AudioSelector.RoleDefault;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != SetDefaultAudioCommand)
        {
            error = $"Usage: {Flag} {SetDefaultAudioCommand} --id ID [--role default|communications|all]";
            return false;
        }

        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            string value = args[index + 1];
            switch (args[index])
            {
                case "--id" when value.Length > 0:
                    id = value;
                    break;
                case "--role" when AudioSelector.TryParseRole(value, out role, out _, out error):
                    break;
                default:
                    error = error ?? $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        if (id.Length == 0)
        {
            error = "--id is required.";
            return false;
        }

        return true;
    }

    private static int RunListAudio(string[] args)
    {
        if (!TryParseListAudio(args, out AudioSelector selector, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<AudioEndpointListResult> result = AudioEndpoints.List(selector);
        if (!result.Success)
        {
            return Reject(result.Error!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunSetDefaultAudio(string[] args)
    {
        // Gated by AudioTools from the service's VestigoOptions. This process is the console
        // user and must not open the machine settings file (it holds API tokens).
        if (!TryParseSetDefaultAudio(args, out string id, out string role, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<SetDefaultAudioResult> result = AudioEndpoints.SetDefault(id, role);
        if (!result.Success)
        {
            return Reject(result.Error!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }
}
