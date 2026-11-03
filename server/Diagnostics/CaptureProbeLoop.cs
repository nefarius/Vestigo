using System.Diagnostics;

namespace Vestigo.Diagnostics;

/// <summary>Acquires frames until the duration elapses. Disposes the source on every exit, including cancellation.</summary>
internal interface ICaptureFrameSource : IDisposable
{
    /// <summary>True when a frame arrived. A timeout HRESULT is not a failure.</summary>
    bool TryAcquire(int timeoutMs, out int hresult);
}

internal static class CaptureProbeLoop
{
    public static CaptureMeasured Run(ICaptureFrameSource source, TimeSpan duration, CancellationToken cancellationToken)
    {
        int frames = 0;
        int? first = null;
        int final = 0;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = (int)Math.Max(1, (duration - clock.Elapsed).TotalMilliseconds);
                if (!source.TryAcquire(Math.Min(remaining, 100), out int hr))
                {
                    if (hr == CaptureHresults.WaitTimeout)
                    {
                        continue;
                    }

                    final = hr;
                    break;
                }

                frames++;
                first ??= (int)clock.ElapsedMilliseconds;
            }

            return new CaptureMeasured(frames, first, final == 0 ? null : CaptureHresults.Hex(final), CaptureHresults.Name(final));
        }
        finally
        {
            source.Dispose();
        }
    }
}
