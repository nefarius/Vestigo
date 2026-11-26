using Microsoft.Diagnostics.Tracing;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Analyzes a root-confined <c>.etl</c> with TraceEvent and the shared kernel aggregator.</summary>
public sealed class WindowsEtlPerformanceAnalyzer(RootPolicy roots, VestigoOptions options) : IEtlPerformanceAnalyzer
{
    public OperationResult<KernelLatencySummary> Analyze(string fullPath, IReadOnlyList<string> groups, int maxEvents)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<KernelLatencySummary>.Fail("windows_required", "ETL performance analysis requires Windows.");
        }

        OperationResult<IReadOnlyList<string>> parsed = KernelTraceGroups.Parse(groups);
        if (!parsed.Success)
        {
            return parsed.FailAs<KernelLatencySummary>();
        }

        PathCheck located = roots.ResolveExisting(fullPath, RootAccess.Read);
        if (!located.Success)
        {
            return OperationResult<KernelLatencySummary>.Fail(located.ErrorCode!, located.Error!);
        }

        if (!located.FullPath!.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<KernelLatencySummary>.Fail("invalid_request", "The file must be a .etl trace.");
        }

        long length;
        try
        {
            FileInfo info = new(located.FullPath);
            if (!info.Exists)
            {
                return OperationResult<KernelLatencySummary>.Fail("etl_unreadable", "The ETL file could not be read.");
            }

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<KernelLatencySummary>.Fail("etl_unreadable", ex.Message);
        }

        if (length > options.Performance.MaxRawEtlBytes)
        {
            return OperationResult<KernelLatencySummary>.Fail("etl_too_large", "The ETL file is larger than Vestigo:Performance:MaxRawEtlBytes.");
        }

        KernelLatencyAggregator aggregator = new(maxEvents);
        try
        {
            using ETWTraceEventSource source = new(located.FullPath);
            KernelTraceEventSink.Attach(source, aggregator, parsed.Value!);
            source.Process();
            aggregator.EventsLost = source.EventsLost > 0;
            TimeSpan duration = source.SessionEndTime - source.SessionStartTime;
            return OperationResult<KernelLatencySummary>.Ok(
                aggregator.Build(Path.GetFileName(located.FullPath), (int)Math.Max(duration.TotalMilliseconds, 0), parsed.Value!, null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<KernelLatencySummary>.Fail("etl_analysis_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
