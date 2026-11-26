using System.ComponentModel;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Performance;

namespace Vestigo.Performance;

/// <summary>Samples English PDH counters through CsWin32. Paths stay locale-independent.</summary>
public sealed class WindowsPdhSampler : ICounterSampler
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtNoCap100 = 0x00008000;

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

            Dictionary<string, List<double>> series = expanded.ToDictionary(path => path, _ => new List<double>(), StringComparer.OrdinalIgnoreCase);
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
                DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                    TimeSpan wait = remaining < interval ? remaining : interval;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }

                    query.Collect();
                    foreach (PdhCounter counter in counters)
                    {
                        if (counter.TryRead(out double value, out string? error))
                        {
                            series[counter.Path].Add(value);
                        }
                        else
                        {
                            errors[counter.Path] = error;
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

            List<CounterSample> samples = [];
            foreach (string path in expanded)
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
            IReadOnlyList<string> expanded = path.Contains('*', StringComparison.Ordinal)
                ? ExpandWildcard(path)
                : [path];
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

    private static IReadOnlyList<string> ExpandWildcard(string path)
    {
        uint size = 0;
        uint first = PInvoke.PdhExpandWildCardPath(null, path, ref size, 0);
        if (first != PdhMoreData && first != 0)
        {
            return [];
        }

        if (size < 2)
        {
            return [];
        }

        char[] buffer = new char[size];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                uint status = PInvoke.PdhExpandWildCardPath(null, path, new PZZWSTR(pointer), ref size, 0);
                if (status != 0)
                {
                    return [];
                }
            }
        }

        List<string> result = [];
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break;
            }

            result.Add(new string(buffer, start, i - start));
            start = i + 1;
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

        public bool TryRead(out double value, out string? error)
        {
            value = 0;
            error = null;
            uint status = PInvoke.PdhGetFormattedCounterValue(
                handle,
                PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)PdhFmtNoCap100,
                out PDH_FMT_COUNTERVALUE formatted);
            if (status != 0)
            {
                error = $"PDH status 0x{status:X8}";
                return false;
            }

            value = formatted.doubleValue;
            return double.IsFinite(value);
        }

        public void Dispose() => _ = PInvoke.PdhRemoveCounter(handle);
    }
}
