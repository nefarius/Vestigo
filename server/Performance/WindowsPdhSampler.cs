using System.ComponentModel;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.System.Performance;

namespace Vestigo.Performance;

/// <summary>Samples English PDH counters through CsWin32. Paths stay locale-independent.</summary>
public sealed partial class WindowsPdhSampler : ICounterSampler
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    public async Task<OperationResult<CounterSampleResult>> SampleAsync(
        IReadOnlyList<string> paths,
        TimeSpan duration,
        TimeSpan interval,
        int maxPaths,
        string preset,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
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
            Dictionary<string, string?> errors = new(StringComparer.OrdinalIgnoreCase);
            using PdhQuery query = PdhQuery.Open();
            List<PdhCounter> counters = [];
            try
            {
                foreach (string path in expanded)
                {
                    try
                    {
                        counters.Add(query.Add(path));
                    }
                    catch (Win32Exception ex)
                    {
                        errors[path] = ex.Message;
                    }
                }

                if (counters.Count == 0)
                {
                    return OperationResult<CounterSampleResult>.Fail("counter_not_found", "No counters could be added to the PDH query.");
                }

                query.Collect();
                long started = TimeProvider.System.GetTimestamp();
                while (TimeProvider.System.GetElapsedTime(started) < duration)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = duration - TimeProvider.System.GetElapsedTime(started);
                    TimeSpan wait = remaining < interval ? remaining : interval;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }

                    query.Collect();
                    foreach (PdhCounter counter in counters)
                    {
                        foreach ((string path, double value, string? error) in counter.ReadSamples())
                        {
                            if (!series.TryGetValue(path, out List<double>? values))
                            {
                                if (series.Count >= maxPaths && !series.ContainsKey(path))
                                {
                                    truncated = true;
                                    continue;
                                }

                                values = [];
                                series[path] = values;
                            }

                            if (error is not null)
                            {
                                errors[path] = error;
                                continue;
                            }

                            values.Add(value);
                        }
                    }
                }
            }
            finally
            {
                foreach (PdhCounter counter in counters)
                {
                    counter.Dispose();
                }
            }

            foreach (string path in errors.Keys)
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
                errors.TryGetValue(path, out string? error);
                samples.Add(new CounterSample(
                    path,
                    PercentileStats.From(values ?? []),
                    error is null ? "ok" : "error",
                    error));
            }

            return OperationResult<CounterSampleResult>.Ok(new CounterSampleResult(
                preset,
                (int)duration.TotalMilliseconds,
                (int)interval.TotalMilliseconds,
                samples.Count,
                truncated,
                samples));
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

    private static List<string> Expand(IReadOnlyList<string> paths, int maxPaths, out bool truncated)
    {
        truncated = false;
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            IReadOnlyList<string> expanded = [path];
            foreach (string item in expanded)
            {
                if (!seen.Add(item))
                {
                    continue;
                }

                if (result.Count >= maxPaths)
                {
                    truncated = true;
                    return result;
                }

                result.Add(item);
            }
        }

        return result;
    }

    private sealed class PdhQuery : IDisposable
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

        public PdhCounter Add(string path)
        {
            uint status = PInvoke.PdhAddEnglishCounter(_handle, path, 0, out PDH_HCOUNTER counter);
            if (status != 0)
            {
                throw new Win32Exception(unchecked((int)status), $"PdhAddEnglishCounter failed for '{path}'.");
            }

            return new PdhCounter(path, counter);
        }

        public void Collect()
        {
            using PdhCloseQuerySafeHandle.LeaseScope lease = _handle.Lease();
            uint status = PInvoke.PdhCollectQueryData(lease.Value);
            if (status != 0)
            {
                throw new Win32Exception(unchecked((int)status), "PdhCollectQueryData failed.");
            }
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

    private sealed class PdhCounter(string path, PDH_HCOUNTER handle) : IDisposable
    {
        public string Path { get; } = path;

        public IReadOnlyList<(string Path, double Value, string? Error)> ReadSamples()
        {
            if (!Path.Contains('*', StringComparison.Ordinal))
            {
                uint status = PInvoke.PdhGetFormattedCounterValue(
                    handle,
                    PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)PdhFmtNoCap100,
                    out PDH_FMT_COUNTERVALUE formatted);
                if (status != 0)
                {
                    return [(Path, 0, $"PDH status 0x{status:X8}")];
                }

                return double.IsFinite(formatted.doubleValue)
                    ? [(Path, formatted.doubleValue, null)]
                    : [(Path, 0, "PDH returned a non-finite value")];
            }

            return ReadWildcardSamples();
        }

        private IReadOnlyList<(string Path, double Value, string? Error)> ReadWildcardSamples()
        {
            uint size = 0;
            uint count = 0;
            uint format = (uint)(PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)PdhFmtNoCap100);
            uint status = NativePdh.PdhGetFormattedCounterArray(handle, format, ref size, out count, 0);
            if (status != PdhMoreData && status != 0)
            {
                return [(Path, 0, $"PDH status 0x{status:X8}")];
            }

            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = NativePdh.PdhGetFormattedCounterArray(handle, format, ref size, out count, buffer);
                if (status != 0)
                {
                    return [(Path, 0, $"PDH status 0x{status:X8}")];
                }

                List<(string Path, double Value, string? Error)> result = [];
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
                    if ((formatted.CStatus != PdhCstatusValidData && formatted.CStatus != PdhCstatusNewData)
                        || !double.IsFinite(formatted.doubleValue))
                    {
                        result.Add((english, 0, $"PDH status 0x{formatted.CStatus:X8}"));
                        continue;
                    }

                    result.Add((english, formatted.doubleValue, null));
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
