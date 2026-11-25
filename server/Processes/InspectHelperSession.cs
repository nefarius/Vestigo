using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Jobs;
using Vestigo.Security;
using Vestigo.Vision;

namespace Vestigo.Processes;

/// <summary>One capture on an inspection helper that stays up for a single observed <c>send_input</c> call.</summary>
internal interface IInspectCaptureSession : IAsyncDisposable
{
    Task<OperationResult<InspectCaptureResult>> InspectAsync(
        ScreenCaptureRequest capture,
        bool uia,
        bool ocr,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken);
}

/// <summary>Starts <c>inspect-session</c> on the console user's desktop and reads one JSON result per request.</summary>
internal static class InspectHelperSession
{
    internal static Task<OperationResult<IInspectCaptureSession>> StartAsync(
        IProcessLauncher launcher,
        bool elevated,
        string? desktop,
        CancellationToken cancellationToken,
        TimeSpan? shutdownGrace = null)
    {
        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return Task.FromResult(available.FailAs<IInspectCaptureSession>());
        }

        string directory;
        try
        {
            directory = StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.SpoolDirectory, "helper"));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(OperationResult<IInspectCaptureSession>.Fail("untrusted_path", ex.Message));
        }

        string outputPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
        AnonymousPipeServerStream? commands = null;
        AnonymousPipeServerStream? results = null;
        CancellationTokenSource? cancellation = null;
        try
        {
            File.WriteAllBytes(outputPath, []);
            commands = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            results = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            (string executable, List<string> prefix) = SessionHelperClient.Host();
            List<string> arguments = [.. prefix, SessionHelper.Flag, SessionHelper.InspectSessionCommand];
            LaunchRequest request = new(
                executable,
                arguments,
                Path.GetDirectoryName(executable),
                new Dictionary<string, string>(),
                LaunchContext.Interactive,
                elevated,
                desktop,
                StandardInput: commands.ClientSafePipeHandle,
                ReleaseInheritedInput: commands.DisposeLocalCopyOfClientHandle,
                StandardOutput: results.ClientSafePipeHandle,
                ReleaseInheritedOutput: results.DisposeLocalCopyOfClientHandle);
            JobContext output = new("inspect-session", outputPath, 1024 * 1024);
            Task<int> run = launcher.RunAsync(request, output, cancellation.Token);
            if (run.IsFaulted)
            {
                Observe(run);
                ReleaseStart(commands, results, cancellation, outputPath);
                return Task.FromResult(LaunchFailure(run));
            }

            InspectHelperChannel channel = new(commands, results, () => cancellation.Cancel());
            IInspectCaptureSession session = new RunningInspectSession(
                channel,
                run,
                cancellation,
                outputPath,
                shutdownGrace ?? TimeSpan.FromSeconds(5));
            return Task.FromResult(OperationResult<IInspectCaptureSession>.Ok(session));
        }
        catch (Exception ex)
        {
            ReleaseStart(commands, results, cancellation, outputPath);
            if (ex is ProcessLaunchException launch)
            {
                return Task.FromResult(OperationResult<IInspectCaptureSession>.Fail(launch.Code, launch.Message, launch.Reason));
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(OperationResult<IInspectCaptureSession>.Fail("start_failed", ex.Message));
            }

            return Task.FromException<OperationResult<IInspectCaptureSession>>(ex);
        }
    }

    private static void ReleaseStart(
        AnonymousPipeServerStream? commands,
        AnonymousPipeServerStream? results,
        CancellationTokenSource? cancellation,
        string outputPath)
    {
        try
        {
            commands?.DisposeLocalCopyOfClientHandle();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            results?.DisposeLocalCopyOfClientHandle();
        }
        catch (ObjectDisposedException)
        {
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        commands?.Dispose();
        results?.Dispose();
        try
        {
            File.Delete(outputPath);
        }
        catch (IOException)
        {
        }
    }

    private static OperationResult<IInspectCaptureSession> LaunchFailure(Task<int> run)
    {
        Exception error = run.Exception!.GetBaseException();
        return error is ProcessLaunchException launch
            ? OperationResult<IInspectCaptureSession>.Fail(launch.Code, launch.Message, launch.Reason)
            : OperationResult<IInspectCaptureSession>.Fail("start_failed", error.Message);
    }

    private static void Observe(Task task)
    {
        _ = task.Exception;
    }

    private sealed class RunningInspectSession(
        InspectHelperChannel channel,
        Task<int> run,
        CancellationTokenSource cancellation,
        string outputPath,
        TimeSpan shutdownGrace) : IInspectCaptureSession
    {
        public Task<OperationResult<InspectCaptureResult>> InspectAsync(
            ScreenCaptureRequest capture,
            bool uia,
            bool ocr,
            TimeSpan timeout,
            long maxOutputBytes,
            CancellationToken cancellationToken) =>
            channel.InspectAsync(capture, uia, ocr, timeout, maxOutputBytes, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            bool closed = await channel.TryWriteCloseAsync().ConfigureAwait(false);
            if (!closed)
            {
                cancellation.Cancel();
            }

            try
            {
                await run.WaitAsync(closed ? shutdownGrace : TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ProcessLaunchException)
                {
                }
            }
            catch (Exception)
            {
                cancellation.Cancel();
            }

            await channel.DisposeStreamsAsync().ConfigureAwait(false);
            cancellation.Dispose();
            try
            {
                File.Delete(outputPath);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>JSON-line request and result pipe for <see cref="IInspectCaptureSession"/>.</summary>
internal sealed class InspectHelperChannel(Stream commands, Stream results, Action? onBroken = null) : IInspectCaptureSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StreamWriter _commands = new(commands, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true,
        NewLine = "\n"
    };
    private readonly byte[] _chunk = new byte[8192];
    private int _chunkOffset;
    private int _chunkCount;
    private bool _broken;
    private bool _closed;

    public async Task<OperationResult<InspectCaptureResult>> InspectAsync(
        ScreenCaptureRequest capture,
        bool uia,
        bool ocr,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_broken || _closed)
            {
                return OperationResult<InspectCaptureResult>.Fail("helper_failed", "The inspection helper is no longer running.");
            }

            string requestId = Guid.NewGuid().ToString("N");
            string payload = JsonSerializer.Serialize(
                new InspectSessionRequest(InspectSessionOp.Inspect, requestId, capture, uia, ocr),
                InspectHelperJsonContext.Default.InspectSessionRequest);
            if (Encoding.UTF8.GetByteCount(payload) > SessionHelper.MaxInspectSessionRequestBytes)
            {
                return OperationResult<InspectCaptureResult>.Fail("invalid_request", "The inspect request is too large.");
            }

            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await _commands.WriteLineAsync(payload.AsMemory(), limit.Token).ConfigureAwait(false);
                OperationResult<string> line = await ReadLineAsync(maxOutputBytes, limit.Token).ConfigureAwait(false);
                if (!line.Success)
                {
                    Break();
                    return line.FailAs<InspectCaptureResult>();
                }

                return Parse(line.Value!, requestId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                Break();
                return OperationResult<InspectCaptureResult>.Fail("helper_timeout", "The inspection helper did not finish in time.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Break();
                return OperationResult<InspectCaptureResult>.Fail("helper_failed", "The inspection helper exited.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => DisposeStreamsAsync();

    /// <summary>Writes <c>close</c> when no capture is in flight. Returns false when a capture owns the pipe.</summary>
    internal async Task<bool> TryWriteCloseAsync()
    {
        if (_closed || !await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            if (_broken)
            {
                return false;
            }

            string payload = JsonSerializer.Serialize(
                new InspectSessionRequest(InspectSessionOp.Close, Guid.NewGuid().ToString("N")),
                InspectHelperJsonContext.Default.InspectSessionRequest);
            await _commands.WriteLineAsync(payload.AsMemory()).ConfigureAwait(false);
            _closed = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _broken = true;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask DisposeStreamsAsync()
    {
        _closed = true;
        try
        {
            await _commands.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        try
        {
            await results.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private OperationResult<InspectCaptureResult> Parse(string line, string requestId)
    {
        InspectSessionResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(line, InspectHelperJsonContext.Default.InspectSessionResponse);
        }
        catch (JsonException ex)
        {
            return OperationResult<InspectCaptureResult>.Fail("helper_failed", "The inspection helper returned invalid JSON: " + ex.Message);
        }

        if (response is null || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            Break();
            return OperationResult<InspectCaptureResult>.Fail("helper_failed", "The inspection helper returned an unexpected result.");
        }

        if (!response.Success || response.Result is null)
        {
            return OperationResult<InspectCaptureResult>.Fail(
                string.IsNullOrWhiteSpace(response.ErrorCode) ? "helper_failed" : response.ErrorCode,
                response.Error ?? "The inspection helper rejected the capture.",
                response.Reason);
        }

        return OperationResult<InspectCaptureResult>.Ok(response.Result);
    }

    private async Task<OperationResult<string>> ReadLineAsync(long maxBytes, CancellationToken cancellationToken)
    {
        using MemoryStream line = new();
        bool carriageReturn = false;
        while (true)
        {
            if (_chunkCount == 0)
            {
                _chunkOffset = 0;
                _chunkCount = await results.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_chunkCount == 0)
                {
                    return OperationResult<string>.Fail(
                        "helper_failed",
                        line.Length == 0
                            ? "The inspection helper exited."
                            : "The inspection helper closed its result before the line ended.");
                }
            }

            while (_chunkCount > 0)
            {
                byte value = _chunk[_chunkOffset++];
                _chunkCount--;
                if (carriageReturn)
                {
                    carriageReturn = false;
                    if (value == (byte)'\n')
                    {
                        return OperationResult<string>.Ok(Encoding.UTF8.GetString(line.ToArray()));
                    }

                    if (!TryAppend(line, (byte)'\r', maxBytes))
                    {
                        return OperationResult<string>.Fail("too_large", "The inspection helper output exceeded the limit.");
                    }
                }

                if (value == (byte)'\n')
                {
                    return OperationResult<string>.Ok(Encoding.UTF8.GetString(line.ToArray()));
                }

                if (value == (byte)'\r')
                {
                    carriageReturn = true;
                    continue;
                }

                if (!TryAppend(line, value, maxBytes))
                {
                    return OperationResult<string>.Fail("too_large", "The inspection helper output exceeded the limit.");
                }
            }
        }
    }

    private static bool TryAppend(MemoryStream line, byte value, long maxBytes)
    {
        if (line.Length >= maxBytes)
        {
            return false;
        }

        line.WriteByte(value);
        return true;
    }

    private void Break()
    {
        if (_broken)
        {
            return;
        }

        _broken = true;
        onBroken?.Invoke();
    }
}
