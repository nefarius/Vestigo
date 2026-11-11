using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Desktop;

/// <summary>
///     Keeps one interactive desktop host alive so test-desktop handles survive a stateless MCP call. Commands are
///     JSON lines. Recovery uses a separate one-shot helper because the host is gone after a service interruption.
/// </summary>
public sealed class HelperDesktopHostClient(IProcessLauncher launcher) : IDesktopHostClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _state = new();
    private CancellationTokenSource? _hostCancellation;
    private Task<int>? _hostTask;
    private AnonymousPipeServerStream? _commands;
    private StreamWriter? _commandWriter;
    private string? _statusPath;
    private long _statusOffset;

    public async Task<OperationResult<DesktopCommandResult>> SendAsync(DesktopCommand command, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OperationResult<bool> ready = await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Success)
            {
                return ready.FailAs<DesktopCommandResult>();
            }

            if (_commandWriter is null)
            {
                return OperationResult<DesktopCommandResult>.Fail("desktop_host_lost", "The interactive desktop host is not running.");
            }

            await _commandWriter.WriteLineAsync(JsonSerializer.Serialize(command, SessionHelper.Json).AsMemory(), cancellationToken).ConfigureAwait(false);
            return await WaitForAsync(command.RequestId, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<DesktopCommandResult>.Fail("cancelled", "The desktop-host command was cancelled.");
        }
        finally
        {
            _send.Release();
        }
    }

    public async Task<OperationResult<DesktopCommandResult>> RecoverAsync(DesktopCommand command, CancellationToken cancellationToken)
    {
        await using AnonymousPipeServerStream pipe = new(PipeDirection.Out, HandleInheritability.Inheritable);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, SessionHelper.Json));
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        SessionHelperClient client = new(launcher);
        return await client.RunAsync<DesktopCommandResult>(
            [SessionHelper.Flag, SessionHelper.DesktopRecoverCommand],
            elevated: false,
            TimeSpan.FromSeconds(20),
            1024 * 1024,
            cancellationToken,
            pipe.ClientSafePipeHandle,
            () =>
            {
                pipe.DisposeLocalCopyOfClientHandle();
                pipe.Dispose();
            }).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Task<int>? host;
        lock (_state)
        {
            host = _hostTask;
        }

        if (host is null || host.IsCompleted)
        {
            return;
        }

        try
        {
            DesktopCommand shutdown = new(Guid.NewGuid().ToString("N"), DesktopHostOperations.Shutdown);
            await SendAsync(shutdown, cancellationToken).ConfigureAwait(false);
            await host.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ProcessLaunchException or IOException)
        {
            // The job object ends the host if it is still running.
        }

        CancelHost();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task<OperationResult<bool>> EnsureHostAsync(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_hostTask is { IsCompleted: false })
            {
                return OperationResult<bool>.Ok(true);
            }
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated: false);
        if (!available.Success)
        {
            return available;
        }

        string directory;
        try
        {
            directory = StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.SpoolDirectory, "helper"));
        }
        catch (InvalidOperationException ex)
        {
            return OperationResult<bool>.Fail("untrusted_path", ex.Message);
        }

        string id = Guid.NewGuid().ToString("N");
        (string executable, List<string> prefix) = SessionHelperClient.Host();
        List<string> arguments = [..prefix, SessionHelper.Flag, SessionHelper.DesktopHostCommand];
        CancellationTokenSource cancellation = new();
        JobContext output = new("desktop-host", Path.Combine(directory, "desktop-host-" + id + ".log"), 1024 * 1024);
        await File.WriteAllBytesAsync(output.OutputPath, [], cancellationToken).ConfigureAwait(false);
        AnonymousPipeServerStream commands = new(PipeDirection.Out, HandleInheritability.Inheritable);
        StreamWriter writer = new(commands, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        LaunchRequest request = new(
            executable,
            arguments,
            Path.GetDirectoryName(executable),
            new Dictionary<string, string>(),
            LaunchContext.Interactive,
            false,
            StandardInput: commands.ClientSafePipeHandle,
            ReleaseInheritedInput: commands.DisposeLocalCopyOfClientHandle);
        Task<int> run = launcher.RunAsync(request, output, cancellation.Token);
        lock (_state)
        {
            _hostCancellation = cancellation;
            _hostTask = run;
            _commands = commands;
            _commandWriter = writer;
            _statusPath = output.OutputPath;
            _statusOffset = 0;
        }

        bool readyOk = false;
        try
        {
            OperationResult<DesktopCommandResult> ready = await WaitForAsync(DesktopHostOperations.Ready, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            readyOk = ready.Success;
            return readyOk ? OperationResult<bool>.Ok(true) : ready.FailAs<bool>();
        }
        finally
        {
            if (!readyOk)
            {
                CancelHost();
            }
        }
    }

    private async Task<OperationResult<DesktopCommandResult>> WaitForAsync(string requestId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        StringBuilder pending = new();
        try
        {
            while (true)
            {
                lock (_state)
                {
                    if (_hostTask is { IsCompleted: true } && requestId != DesktopHostOperations.Ready)
                    {
                        return OperationResult<DesktopCommandResult>.Fail("desktop_host_lost", "The interactive desktop host exited.");
                    }
                }

                string? statusPath;
                long offset;
                lock (_state)
                {
                    statusPath = _statusPath;
                    offset = _statusOffset;
                }

                if (statusPath is null)
                {
                    return OperationResult<DesktopCommandResult>.Fail("desktop_host_lost", "The interactive desktop host is not running.");
                }

                string chunk = await ReadNewAsync(statusPath, offset).ConfigureAwait(false);
                if (chunk.Length > 0)
                {
                    lock (_state)
                    {
                        _statusOffset += Encoding.UTF8.GetByteCount(chunk);
                    }

                    pending.Append(chunk);
                    while (TakeLine(pending, out string? line))
                    {
                        DesktopCommandResult? result = Parse(line!);
                        if (result is not null && string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
                        {
                            return OperationResult<DesktopCommandResult>.Ok(result);
                        }
                    }
                }

                await Task.Delay(30, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<DesktopCommandResult>.Fail("helper_timeout", "The desktop host did not answer in time.");
        }
    }

    private void CancelHost()
    {
        CancellationTokenSource? cancellation;
        lock (_state)
        {
            cancellation = _hostCancellation;
            _hostTask = null;
            _hostCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        StreamWriter? writer;
        AnonymousPipeServerStream? commands;
        lock (_state)
        {
            writer = _commandWriter;
            commands = _commands;
            _commandWriter = null;
            _commands = null;
        }

        writer?.Dispose();
        commands?.Dispose();
    }

    private static async Task<string> ReadNewAsync(string path, long offset)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length)
        {
            offset = stream.Length;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static bool TakeLine(StringBuilder pending, out string? line)
    {
        string text = pending.ToString();
        int newline = text.IndexOf('\n');
        if (newline < 0)
        {
            line = null;
            return false;
        }

        line = text[..newline].Trim();
        pending.Clear();
        pending.Append(text[(newline + 1)..]);
        return true;
    }

    private static DesktopCommandResult? Parse(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopCommandResult>(line, SessionHelper.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
