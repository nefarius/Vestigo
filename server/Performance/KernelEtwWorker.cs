using System.Text.Json;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>
///     Captures kernel ETW outside the service process. A native TraceEvent fault terminates this
///     worker instead of Vestigo.
/// </summary>
public static class KernelEtwWorker
{
    public const string Flag = "--perf-etw-worker";
    public const string SessionPrefix = "Vestigo-Perf-";

    public static bool IsOwnedSession(string name) =>
        name.StartsWith(SessionPrefix, StringComparison.Ordinal)
        && name.Length == SessionPrefix.Length + 12
        && name[SessionPrefix.Length..].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static int Run(string[] args)
    {
        if (args.Length >= 3 && args[1] == "stop" && IsOwnedSession(args[2]))
        {
            try
            {
                TraceEventSession.GetActiveSession(args[2])?.Stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            return 0;
        }

        if (args.Length >= 6
            && args[1] == "capture"
            && IsOwnedSession(args[2])
            && int.TryParse(args[3], out int durationMs)
            && int.TryParse(args[4], out int maxEvents))
        {
            string[] groups = args[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Capture(args[2], durationMs, maxEvents, groups);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(OperationResult<KernelLatencySummary>.Fail(
            "invalid_request",
            "Usage: --perf-etw-worker capture <session> <durationMs> <maxEvents> <groups> | stop <session>")));
        return 2;
    }

    private static int Capture(string sessionName, int durationMs, int maxEvents, IReadOnlyList<string> groups)
    {
        if (!OperatingSystem.IsWindows())
        {
            Write(OperationResult<KernelLatencySummary>.Fail("windows_required", "Kernel ETW capture requires Windows."));
            return 2;
        }

        OperationResult<IReadOnlyList<string>> parsed = KernelTraceGroups.Parse(groups);
        if (!parsed.Success)
        {
            Write(parsed.FailAs<KernelLatencySummary>());
            return 2;
        }

        KernelLatencyAggregator aggregator = new(maxEvents);
        try
        {
            using TraceEventSession session = new(sessionName);
            KernelTraceEventParser.Keywords keywords = KeywordsFor(parsed.Value!);
            try
            {
                session.EnableKernelProvider(keywords);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Write(OperationResult<KernelLatencySummary>.Fail("kernel_logger_in_use", ex.Message));
                return 2;
            }

            KernelTraceEventSink.Attach(session.Source, aggregator, parsed.Value!);
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(durationMs));
            cts.Token.Register(() =>
            {
                try
                {
                    session.Stop();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
            session.Source.Process();
            aggregator.EventsLost = session.EventsLost > 0;
            Write(OperationResult<KernelLatencySummary>.Ok(
                aggregator.Build(sessionName, durationMs, parsed.Value!, null)));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(OperationResult<KernelLatencySummary>.Fail("kernel_trace_failed", $"{ex.GetType().Name}: {ex.Message}"));
            return 2;
        }
    }

    internal static KernelTraceEventParser.Keywords KeywordsFor(IReadOnlyList<string> groups)
    {
        KernelTraceEventParser.Keywords keywords = KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.Process;
        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DiskIo))
        {
            keywords |= KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit;
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.FileIo))
        {
            keywords |= KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit;
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DpcIsr))
        {
            keywords |= KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt;
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.Scheduler))
        {
            keywords |= KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Dispatcher;
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.HardFaults))
        {
            keywords |= KernelTraceEventParser.Keywords.MemoryHardFaults;
        }

        return keywords;
    }

    private static void Write(OperationResult<KernelLatencySummary> result) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
