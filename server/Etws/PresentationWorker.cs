using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;

namespace Vestigo.Etws;

/// <summary>
///     Decodes presentation ETW outside the service process. A native metadata fault in
///     <c>Nefarius.Utilities.ETW</c> terminates this process instead of Vestigo.
/// </summary>
public static class PresentationWorker
{
    public const string Flag = "--presentation-worker";

    public static bool IsOwnedSession(string name) =>
        name.Length == CaptureManager.SessionPrefix.Length + 12
        && name.StartsWith(CaptureManager.SessionPrefix + "P", StringComparison.Ordinal)
        && name[(CaptureManager.SessionPrefix.Length + 1)..].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static int Run(string[] args)
    {
        if (args.Length >= 3 && args[1] == "stop")
        {
            return Stop(args[2]);
        }

        if (args.Length is 6 or 9
            && args[1] == "observe"
            && IsOwnedSession(args[2])
            && int.TryParse(args[3], out int processId)
            && long.TryParse(args[4], out long window)
            && int.TryParse(args[5], out int durationMs)
            && TryReadTraceSettings(args, out uint bufferKb, out uint flushSeconds, out string clock))
        {
            return Observe(args[2], processId, window == 0 ? null : window, durationMs, bufferKb, flushSeconds, clock);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(OperationResult<PresentationObservation>.Fail(
            "invalid_request",
            "Usage: --presentation-worker observe <session> <pid> <window|0> <durationMs> | stop <session>"), SessionHelperJson()));
        return 2;
    }

    private static int Observe(
        string sessionName,
        int processId,
        long? window,
        int durationMs,
        uint bufferKb,
        uint flushSeconds,
        string clock)
    {
        if (!OperatingSystem.IsWindows())
        {
            Write(OperationResult<PresentationObservation>.Fail("windows_required", "Presentation observation requires Windows."));
            return 2;
        }

        PresentationSessionRelease release = new();
        PresentationObserver observer = new(
            new EtwRealtimeTraceSessionFactory(),
            new VestigoOptions
            {
                BufferSizeKb = bufferKb,
                FlushTimerSeconds = flushSeconds,
                ClockResolution = clock
            },
            TimeProvider.System,
            NullLogger<PresentationObserver>.Instance);
        OperationResult<PresentationObservation> result = observer.ObserveAsync(
            processId,
            window,
            TimeSpan.FromMilliseconds(durationMs),
            CancellationToken.None,
            sessionName,
            manageCheckpoint: false,
            release).GetAwaiter().GetResult();
        Write(result);
        return result.Success && release.Released ? 0 : 2;
    }

    private static bool TryReadTraceSettings(string[] args, out uint bufferKb, out uint flushSeconds, out string clock)
    {
        bufferKb = 64;
        flushSeconds = 1;
        clock = "QueryPerformanceCounter";
        if (args.Length == 6)
        {
            return true;
        }

        if (uint.TryParse(args[6], out bufferKb)
            && bufferKb > 0
            && uint.TryParse(args[7], out flushSeconds)
            && flushSeconds > 0
            && args[8].Length is > 0 and <= 64
            && args[8].All(char.IsAsciiLetter))
        {
            clock = args[8];
            return true;
        }

        bufferKb = 0;
        flushSeconds = 0;
        clock = string.Empty;
        return false;
    }

    private static int Stop(string sessionName)
    {
        if (!IsOwnedSession(sessionName))
        {
            Write(OperationResult<bool>.Fail("invalid_request", "Presentation session name is not owned by Vestigo."));
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Write(OperationResult<bool>.Ok(true));
            return 0;
        }

        try
        {
            new EtwRealtimeTraceSessionFactory().StopOrphan(sessionName);
            Write(OperationResult<bool>.Ok(true));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(OperationResult<bool>.Fail("session_unavailable", ex.Message));
            return 1;
        }
    }

    private static void Write<T>(OperationResult<T> result) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(result, SessionHelperJson()));

    private static JsonSerializerOptions SessionHelperJson() => new(JsonSerializerDefaults.Web);
}

/// <summary>One worker invocation. The service uses this boundary so a decoder crash stays in the child.</summary>
public interface IPresentationProcess
{
    Task<PresentationProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record PresentationProcessResult(
    int? ExitCode,
    string Output,
    bool Cancelled,
    bool TimedOut,
    bool HostFailed = false,
    bool Started = true,
    bool TerminationUncertain = false);

/// <summary>Starts <c>Vestigo.exe --presentation-worker</c> and bounds its output.</summary>
public sealed class ProcessPresentationHost : IPresentationProcess
{
    public const int MaxOutputBytes = 1024 * 1024;

    public async Task<PresentationProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        (string executable, List<string> prefix) = Vestigo.Processes.SessionHelperClient.Host();
        using Process process = new();
        process.StartInfo.FileName = executable;
        process.StartInfo.WorkingDirectory = AppContext.BaseDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (string argument in prefix.Concat(arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        bool started = false;
        try
        {
            if (!process.Start())
            {
                return new PresentationProcessResult(null, string.Empty, false, false, HostFailed: true, Started: false);
            }

            started = true;
            _ = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            Task<string> output = ReadBoundedAsync(process.StandardOutput);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool killed = TryKill(process);
                (string partial, bool drained) = await DrainAsync(output).ConfigureAwait(false);
                return new PresentationProcessResult(
                    null,
                    partial,
                    cancellationToken.IsCancellationRequested,
                    !cancellationToken.IsCancellationRequested,
                    TerminationUncertain: !killed || !drained);
            }

            (string text, bool completed) = await DrainAsync(output).ConfigureAwait(false);
            return new PresentationProcessResult(process.ExitCode, text, false, false, TerminationUncertain: !completed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool killed = started && TryKill(process);
            return new PresentationProcessResult(
                null,
                string.Empty,
                false,
                false,
                HostFailed: true,
                Started: started,
                TerminationUncertain: started && !killed);
        }
    }

    private static async Task<(string Output, bool Completed)> DrainAsync(Task<string> output)
    {
        try
        {
            return (await output.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false), true);
        }
        catch (TimeoutException)
        {
            return (string.Empty, false);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        using StringWriter writer = new();
        int total = 0;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int take = Math.Min(read, Math.Max(0, MaxOutputBytes - total));
            if (take > 0)
            {
                await writer.WriteAsync(buffer.AsMemory(0, take)).ConfigureAwait(false);
                total += take;
            }
        }

        return writer.ToString();
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
