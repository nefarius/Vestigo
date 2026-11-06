using System.ComponentModel;

namespace Vestigo.WindowsServicing;

/// <summary>
/// One native call at a time, with no waiting queue. Timeout/cancellation stop waiting, not native work.
/// The slot remains held until the native call and COM cleanup finish, preventing runaway worker growth.
/// </summary>
public sealed class NativeDiagnosticWorker
{
    private int _busy;

    public async Task<OperationResult<T>> Run<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return OperationResult<T>.Fail("servicing_busy", "Another native diagnostic is still running; retry after it completes.");

        TaskCompletionSource<OperationResult<T>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            OperationResult<T> result;
            try { result = OperationResult<T>.Ok(work()); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                string nativeCode = ex is Win32Exception win32 ? $" (Win32 {win32.NativeErrorCode})" : "";
                result = OperationResult<T>.Fail("native_diagnostic_failed", $"{ex.GetType().Name}: 0x{ex.HResult:X8}{nativeCode}");
            }
            finally { Volatile.Write(ref _busy, 0); }
            completion.TrySetResult(result);
        }) { IsBackground = true, Name = "Vestigo Windows servicing diagnostic" };
        try
        {
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        catch
        {
            Volatile.Write(ref _busy, 0);
            throw;
        }
        try { return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return OperationResult<T>.Fail("servicing_timeout", "Stopped waiting. The native diagnostic may still be running and retains its concurrency slot.");
        }
    }
}
