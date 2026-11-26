using System.ComponentModel;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.System.Performance;

namespace Vestigo.Performance;

/// <summary>Samples English PDH counters through CsWin32. Paths stay locale-independent.</summary>
public sealed partial class WindowsPdhSampler : ICounterSampler
{
    internal const uint PdhCstatusValidData = PInvoke.PDH_CSTATUS_VALID_DATA;
    internal const uint PdhCstatusNewData = PInvoke.PDH_CSTATUS_NEW_DATA;
    internal const uint PdhCstatusNoInstance = PInvoke.PDH_CSTATUS_NO_INSTANCE;
    internal const uint PdhMoreData = PInvoke.PDH_MORE_DATA;
    internal const uint PdhNoData = PInvoke.PDH_NO_DATA;
    internal const uint PdhCstatusNoObject = PInvoke.PDH_CSTATUS_NO_OBJECT;
    internal const uint PdhCstatusNoCounter = PInvoke.PDH_CSTATUS_NO_COUNTER;
    internal const uint PdhInvalidData = PInvoke.PDH_INVALID_DATA;

    // PDH_FMT_NOCAP100 is not present in the pinned Win32Metadata.
    private const uint PdhFmtNoCap100 = 0x00008000;

    private readonly IPdhQueryFactory _factory;
    private readonly TimeProvider _time;
    private readonly bool _requireWindows;

    public WindowsPdhSampler()
        : this(Win32PdhQueryFactory.Instance, TimeProvider.System, requireWindows: true)
    {
    }

    internal WindowsPdhSampler(IPdhQueryFactory factory, TimeProvider? time = null, bool requireWindows = false)
    {
        _factory = factory;
        _time = time ?? TimeProvider.System;
        _requireWindows = requireWindows;
    }

    public async Task<OperationResult<CounterSampleResult>> SampleAsync(
        IReadOnlyList<string> paths,
        TimeSpan duration,
        TimeSpan interval,
        int maxPaths,
        string preset,
        CancellationToken cancellationToken)
    {
        if (_requireWindows && !OperatingSystem.IsWindows())
        {
            return OperationResult<CounterSampleResult>.Fail("windows_required", "Performance counters require Windows.");
        }

        List<string> requested = [];
        foreach (string path in paths)
        {
            string? error = PdhCounterPath.Validate(path);
            if (error is not null)
            {
                return OperationResult<CounterSampleResult>.Fail("invalid_request", error);
            }

            requested.Add(path.Trim());
        }

        if (requested.Count == 0)
        {
            return OperationResult<CounterSampleResult>.Fail("invalid_request", "Provide a preset or at least one counter path.");
        }

        try
        {
            List<string> expanded = Expand(requested, maxPaths, out bool truncated);
            if (expanded.Count == 0)
            {
                return OperationResult<CounterSampleResult>.Fail("counter_not_found", "No counters matched the requested English PDH paths.");
            }

            Dictionary<string, List<double>> series = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CounterIssue> issues = new(StringComparer.OrdinalIgnoreCase);
            using IPdhQuery query = _factory.Open();
            List<IPdhCounter> counters = [];
            try
            {
                foreach (string path in expanded)
                {
                    (IPdhCounter? counter, uint addStatus) = query.TryAdd(path);
                    if (counter is not null)
                    {
                        counters.Add(counter);
                    }
                    else
                    {
                        issues[path] = ClassifyStatus(addStatus, $"PdhAddEnglishCounter failed for '{path}'");
                    }
                }

                if (counters.Count == 0)
                {
                    if (issues.Values.All(issue => issue.Status is "unsupported" or "no_data"))
                    {
                        return BuildResult(preset, TimeSpan.Zero, interval, maxPaths, truncated, series, issues);
                    }

                    return OperationResult<CounterSampleResult>.Fail("counter_not_found", "No counters could be added to the PDH query.");
                }

                uint initialCollect = query.Collect();
                if (initialCollect == PdhNoData)
                {
                    CounterIssue noData = ClassifyStatus(initialCollect, "PdhCollectQueryData returned no data");
                    foreach (IPdhCounter counter in counters)
                    {
                        issues.TryAdd(counter.Path, noData);
                    }

                    return BuildResult(preset, TimeSpan.Zero, interval, maxPaths, truncated, series, issues);
                }

                if (initialCollect != 0)
                {
                    return OperationResult<CounterSampleResult>.Fail(
                        "pdh_failed",
                        $"PdhCollectQueryData failed (PDH status 0x{initialCollect:X8}).");
                }

                long started = _time.GetTimestamp();
                while (_time.GetElapsedTime(started) < duration)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = duration - _time.GetElapsedTime(started);
                    TimeSpan wait = remaining < interval ? remaining : interval;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    }

                    uint collectStatus = query.Collect();
                    if (collectStatus == PdhNoData)
                    {
                        CounterIssue noData = ClassifyStatus(collectStatus, "PdhCollectQueryData returned no data");
                        foreach (IPdhCounter counter in counters)
                        {
                            issues.TryAdd(counter.Path, noData);
                        }

                        continue;
                    }

                    if (collectStatus != 0)
                    {
                        return OperationResult<CounterSampleResult>.Fail(
                            "pdh_failed",
                            $"PdhCollectQueryData failed (PDH status 0x{collectStatus:X8}).");
                    }

                    foreach (IPdhCounter counter in counters)
                    {
                        foreach (PdhSampleReading sample in counter.ReadSamples())
                        {
                            if (!series.TryGetValue(sample.Path, out List<double>? values))
                            {
                                if (series.Count >= maxPaths)
                                {
                                    truncated = true;
                                    continue;
                                }

                                values = [];
                                series[sample.Path] = values;
                            }

                            if (sample.Issue is not null)
                            {
                                issues[sample.Path] = sample.Issue;
                                continue;
                            }

                            values.Add(sample.Value);
                        }
                    }
                }
            }
            finally
            {
                foreach (IPdhCounter counter in counters)
                {
                    counter.Dispose();
                }
            }

            return BuildResult(preset, duration, interval, maxPaths, truncated, series, issues);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            return OperationResult<CounterSampleResult>.Fail("pdh_failed", ex.Message);
        }
    }

    private static OperationResult<CounterSampleResult> BuildResult(
        string preset,
        TimeSpan duration,
        TimeSpan interval,
        int maxPaths,
        bool truncated,
        Dictionary<string, List<double>> series,
        Dictionary<string, CounterIssue> issues)
    {
        foreach (string path in issues.Keys)
        {
            if (series.ContainsKey(path))
            {
                continue;
            }

            if (series.Count >= maxPaths)
            {
                truncated = true;
                continue;
            }

            series[path] = [];
        }

        List<CounterSample> samples = [];
        foreach (string path in series.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            series.TryGetValue(path, out List<double>? values);
            issues.TryGetValue(path, out CounterIssue? issue);
            bool hasValues = values is { Count: > 0 };
            samples.Add(new CounterSample(
                path,
                PercentileStats.From(values ?? []),
                hasValues ? "ok" : issue?.Status ?? "no_data",
                hasValues ? null : issue?.Error ?? "No counter samples were collected."));
        }

        return OperationResult<CounterSampleResult>.Ok(new CounterSampleResult(
            preset,
            (int)duration.TotalMilliseconds,
            (int)interval.TotalMilliseconds,
            samples.Count,
            truncated,
            samples));
    }

    internal static CounterIssue ClassifyStatus(uint status, string context) => status switch
    {
        PdhCstatusNoObject or PdhCstatusNoCounter => new CounterIssue(
            "unsupported",
            $"Counter or object is not exposed on this host (PDH status 0x{status:X8})."),
        PdhNoData or PdhCstatusNoInstance => new CounterIssue(
            "no_data",
            $"No matching counter instances or data available (PDH status 0x{status:X8})."),
        _ => new CounterIssue("error", $"{context} (PDH status 0x{status:X8}).")
    };

    private static List<string> Expand(IReadOnlyList<string> paths, int maxPaths, out bool truncated)
    {
        truncated = false;
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!seen.Add(path))
            {
                continue;
            }

            if (result.Count >= maxPaths)
            {
                truncated = true;
                return result;
            }

            result.Add(path);
        }

        return result;
    }

    internal sealed record CounterIssue(string Status, string Error);

    internal readonly record struct PdhSampleReading(string Path, double Value, CounterIssue? Issue);

    internal interface IPdhQueryFactory
    {
        IPdhQuery Open();
    }

    internal interface IPdhQuery : IDisposable
    {
        (IPdhCounter? Counter, uint Status) TryAdd(string path);

        uint Collect();
    }

    internal interface IPdhCounter : IDisposable
    {
        string Path { get; }

        IReadOnlyList<PdhSampleReading> ReadSamples();
    }

    private sealed class Win32PdhQueryFactory : IPdhQueryFactory
    {
        public static Win32PdhQueryFactory Instance { get; } = new();

        public IPdhQuery Open() => PdhQuery.Open();
    }

    private sealed class PdhQuery : IPdhQuery
    {
        private readonly PdhCloseQuerySafeHandle _handle;
        private bool _disposed;

        private PdhQuery(PdhCloseQuerySafeHandle handle) => _handle = handle;

        public static PdhQuery Open()
        {
            uint status = PInvoke.PdhOpenQuery(null, 0, out PdhCloseQuerySafeHandle handle);
            if (status != 0)
            {
                handle.Dispose();
                throw new Win32Exception(unchecked((int)status), "PdhOpenQuery failed.");
            }

            return new PdhQuery(handle);
        }

        public (IPdhCounter? Counter, uint Status) TryAdd(string path)
        {
            uint status = PInvoke.PdhAddEnglishCounter(_handle, path, 0, out PDH_HCOUNTER counter);
            return status == 0
                ? (new PdhCounter(path, counter), 0)
                : (null, status);
        }

        public uint Collect()
        {
            using PdhCloseQuerySafeHandle.LeaseScope lease = _handle.Lease();
            return PInvoke.PdhCollectQueryData(lease.Value);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }
    }

    private sealed class PdhCounter(string path, PDH_HCOUNTER handle) : IPdhCounter
    {
        public string Path { get; } = path;

        public IReadOnlyList<PdhSampleReading> ReadSamples()
        {
            if (!Path.Contains('*', StringComparison.Ordinal))
            {
                uint status = PInvoke.PdhGetFormattedCounterValue(
                    handle,
                    PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)PdhFmtNoCap100,
                    out PDH_FMT_COUNTERVALUE formatted);
                if (status != 0)
                {
                    uint effective = status == PdhInvalidData
                        && formatted.CStatus is PdhCstatusNoObject or PdhCstatusNoCounter or PdhNoData or PdhCstatusNoInstance
                        ? formatted.CStatus
                        : status;
                    return [new PdhSampleReading(Path, 0, ClassifyStatus(effective, "PdhGetFormattedCounterValue failed"))];
                }

                if (formatted.CStatus != PdhCstatusValidData && formatted.CStatus != PdhCstatusNewData)
                {
                    return [new PdhSampleReading(Path, 0, ClassifyStatus(formatted.CStatus, "PDH counter sample failed"))];
                }

                return double.IsFinite(formatted.doubleValue)
                    ? [new PdhSampleReading(Path, formatted.doubleValue, null)]
                    : [new PdhSampleReading(Path, 0, new CounterIssue("error", "PDH returned a non-finite value."))];
            }

            return ReadWildcardSamples();
        }

        private IReadOnlyList<PdhSampleReading> ReadWildcardSamples()
        {
            uint size = 0;
            uint count = 0;
            uint format = (uint)(PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)PdhFmtNoCap100);
            uint status = NativePdh.PdhGetFormattedCounterArray(handle, format, ref size, out count, 0);
            if (status != PdhMoreData && status != 0)
            {
                return [new PdhSampleReading(Path, 0, ClassifyStatus(status, "PdhGetFormattedCounterArray failed"))];
            }

            if (size == 0 || count == 0)
            {
                return [new PdhSampleReading(Path, 0, new CounterIssue("no_data", "No matching counter instances available."))];
            }

            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = NativePdh.PdhGetFormattedCounterArray(handle, format, ref size, out count, buffer);
                if (status != 0)
                {
                    return [new PdhSampleReading(Path, 0, ClassifyStatus(status, "PdhGetFormattedCounterArray failed"))];
                }

                if (count == 0)
                {
                    return [new PdhSampleReading(Path, 0, new CounterIssue("no_data", "No matching counter instances available."))];
                }

                List<PdhSampleReading> result = [];
                int stride = nint.Size + Marshal.SizeOf<PDH_FMT_COUNTERVALUE>();
                for (uint i = 0; i < count; i++)
                {
                    nint item = buffer + (int)(i * stride);
                    nint namePtr = Marshal.ReadIntPtr(item);
                    string instance = namePtr == 0 ? "*" : Marshal.PtrToStringUni(namePtr) ?? "*";
                    PDH_FMT_COUNTERVALUE formatted = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE>(item + nint.Size);
                    string english = Path.Contains("(*)", StringComparison.Ordinal)
                        ? Path.Replace("(*)", "(" + instance + ")", StringComparison.Ordinal)
                        : Path;
                    if (formatted.CStatus != PdhCstatusValidData && formatted.CStatus != PdhCstatusNewData)
                    {
                        result.Add(new PdhSampleReading(english, 0, ClassifyStatus(formatted.CStatus, "PDH counter sample failed")));
                        continue;
                    }

                    if (!double.IsFinite(formatted.doubleValue))
                    {
                        result.Add(new PdhSampleReading(english, 0, new CounterIssue("error", "PDH returned a non-finite value.")));
                        continue;
                    }

                    result.Add(new PdhSampleReading(english, formatted.doubleValue, null));
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose() => _ = PInvoke.PdhRemoveCounter(handle);
    }

    /// <summary>
    ///     CsWin32 does not emit a usable <c>PDH_FMT_COUNTERVALUE_ITEM_W</c> layout with
    ///     NativeMethods.json <c>allowMarshaling: false</c>.
    /// </summary>
    private static partial class NativePdh
    {
        [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial uint PdhGetFormattedCounterArray(
            nint counter,
            uint format,
            ref uint bufferSize,
            out uint itemCount,
            nint buffer);
    }
}
