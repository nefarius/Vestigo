using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Vestigo.SystemInfo;

/// <summary>
///     SYSTEM-only named-pipe host for the Credential Provider DLL. LogonUI.exe is the only accepted peer.
/// </summary>
public sealed class CredentialProviderHost : BackgroundService, IInteractiveLogonDispatcher
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    private readonly ILogger<CredentialProviderHost> _logger;
    private readonly object _gate = new();
    private ConnectedPeer? _peer;

    public CredentialProviderHost(ILogger<CredentialProviderHost> logger)
    {
        _logger = logger;
        string installDir = AppContext.BaseDirectory;
        CredentialProviderFiles.SweepUnlockedOrphans(installDir, CredentialProviderFiles.ReadRegisteredPath());
    }

    /// <inheritdoc />
    public bool LogonUiConnected
    {
        get
        {
            lock (_gate)
            {
                return _peer is { Pipe.IsConnected: true };
            }
        }
    }

    /// <inheritdoc />
    public bool ProviderInstalled => CredentialProviderFiles.StagingExists(AppContext.BaseDirectory);

    /// <inheritdoc />
    public bool ProviderRegistered => CredentialProviderFiles.IsRegistered(AppContext.BaseDirectory);

    /// <inheritdoc />
    public async Task<OperationResult<string>> DispatchAsync(
        string userName,
        string? domain,
        char[] password,
        string? expectedSid,
        CancellationToken cancellationToken)
    {
        ConnectedPeer? peer;
        lock (_gate)
        {
            peer = _peer is { Pipe.IsConnected: true } connected ? connected : null;
            if (peer is not null)
            {
                if (peer.Busy)
                {
                    return OperationResult<string>.Fail("logon_busy", "An interactive logon is already in progress.");
                }

                peer.Busy = true;
            }
        }

        if (peer is null)
        {
            return OperationResult<string>.Fail(
                "logonui_not_connected",
                "LogonUI is not connected. Interactive logon works from the Windows logon or lock screen.");
        }

        byte[] payload = [];
        try
        {
            if (!PeerIsConsoleSession(peer))
            {
                return OperationResult<string>.Fail(
                    "logonui_not_connected",
                    "LogonUI is not attached to the active console session.");
            }

            if (peer.Hello.Scenario == CredentialProviderProtocol.UsageScenario.Unlock &&
                !string.IsNullOrWhiteSpace(expectedSid) &&
                !peer.Hello.UserSids.Contains(expectedSid, StringComparer.OrdinalIgnoreCase))
            {
                return OperationResult<string>.Fail(
                    "sid_mismatch",
                    "The unlock target is not one of the users on the lock screen.");
            }

            payload = CredentialProviderProtocol.EncodeDispatch(userName, domain ?? "", password, expectedSid);
            await WriteFrameAsync(peer.Pipe, CredentialProviderProtocol.MessageType.Dispatch, payload, cancellationToken)
                .ConfigureAwait(false);
            CredentialProviderProtocol.Frame frame = await ReadFrameAsync(peer.Pipe, cancellationToken).ConfigureAwait(false);
            if (frame.Type != CredentialProviderProtocol.MessageType.Status)
            {
                return OperationResult<string>.Fail("provider_protocol", "The Credential Provider sent an unexpected frame.");
            }

            CredentialProviderProtocol.StatusMessage status = CredentialProviderProtocol.DecodeStatus(frame.Payload);
            return status.Status switch
            {
                CredentialProviderProtocol.ProviderStatus.Submitted or
                    CredentialProviderProtocol.ProviderStatus.Succeeded =>
                    OperationResult<string>.Ok(status.Status.ToString()),
                CredentialProviderProtocol.ProviderStatus.Cancelled =>
                    OperationResult<string>.Fail("cancelled", status.Message ?? "LogonUI cancelled the logon."),
                _ => OperationResult<string>.Fail(
                    "logon_failed",
                    status.Message ?? $"The Credential Provider reported Win32 {status.Win32Error}.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Drop(peer);
            return OperationResult<string>.Fail("cancelled", "The interactive logon was cancelled.");
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Credential Provider pipe closed during dispatch.");
            Drop(peer);
            return OperationResult<string>.Fail("logonui_not_connected", "LogonUI disconnected during dispatch.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            peer.Busy = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                if (!TryAcceptPeer(pipe, out string? reject))
                {
                    _logger.LogWarning("Rejected Credential Provider pipe peer: {Reason}", reject);
                    pipe.Dispose();
                    continue;
                }

                CredentialProviderProtocol.Frame helloFrame = await ReadFrameAsync(pipe, stoppingToken).ConfigureAwait(false);
                if (helloFrame.Type != CredentialProviderProtocol.MessageType.Hello)
                {
                    pipe.Dispose();
                    continue;
                }

                CredentialProviderProtocol.HelloMessage hello = CredentialProviderProtocol.DecodeHello(helloFrame.Payload);
                if (!GetClientProcessId(pipe, out uint processId))
                {
                    pipe.Dispose();
                    continue;
                }

                byte[] status = CredentialProviderProtocol.EncodeStatus(
                    new CredentialProviderProtocol.StatusMessage(
                        CredentialProviderProtocol.ProviderStatus.Connected, 0, null));
                await WriteFrameAsync(pipe, CredentialProviderProtocol.MessageType.Status, status, stoppingToken)
                    .ConfigureAwait(false);

                ConnectedPeer peer = new(pipe, hello, processId);
                lock (_gate)
                {
                    _peer?.Pipe.Dispose();
                    _peer = peer;
                }

                _logger.LogInformation(
                    "LogonUI connected for {Scenario} session {Session}",
                    hello.Scenario,
                    hello.SessionId);

                await WaitUntilDisconnectedAsync(pipe, stoppingToken).ConfigureAwait(false);
                pipe.Dispose();
                pipe = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Credential Provider pipe ended.");
                pipe?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Credential Provider pipe host failed.");
                pipe?.Dispose();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_peer?.Pipe == pipe)
                    {
                        _peer = null;
                    }
                }
            }
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            _peer?.Pipe.Dispose();
            _peer = null;
        }

        base.Dispose();
    }

    private void Drop(ConnectedPeer peer)
    {
        lock (_gate)
        {
            if (_peer == peer)
            {
                _peer = null;
            }
        }

        try
        {
            peer.Pipe.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(
            LocalSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            CredentialProviderIdentity.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: CredentialProviderProtocol.MaxPayloadBytes,
            outBufferSize: CredentialProviderProtocol.MaxPayloadBytes,
            pipeSecurity: security);
    }

    private static bool TryAcceptPeer(NamedPipeServerStream pipe, out string? reject)
    {
        reject = null;
        if (!GetClientProcessId(pipe, out uint processId))
        {
            reject = "client pid unavailable";
            return false;
        }

        if (!TryReadImagePath(processId, out string? image) || image is null)
        {
            reject = "client image unavailable";
            return false;
        }

        string expected = Path.Combine(Environment.SystemDirectory, "LogonUI.exe");
        if (!string.Equals(image, expected, StringComparison.OrdinalIgnoreCase))
        {
            reject = "not LogonUI.exe";
            return false;
        }

        if (!TryReadProcessSid(processId, out string? sid) ||
            !string.Equals(sid, LocalSystem.Value, StringComparison.OrdinalIgnoreCase))
        {
            reject = "not LocalSystem";
            return false;
        }

        return true;
    }

    private static bool GetClientProcessId(NamedPipeServerStream pipe, out uint processId)
    {
        processId = 0;
        return PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out processId);
    }

    private static bool TryReadImagePath(uint processId, out string? path)
    {
        path = null;
        HANDLE handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle.IsNull)
        {
            return false;
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        Span<char> buffer = stackalloc char[520];
        uint length = (uint)buffer.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length) ||
            length == 0)
        {
            return false;
        }

        path = new string(buffer[..(int)length]);
        return true;
    }

    private static unsafe bool TryReadProcessSid(uint processId, out string? sid)
    {
        sid = null;
        HANDLE handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle.IsNull)
        {
            return false;
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            token.Dispose();
            return false;
        }

        using (token)
        {
            uint size = 0;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, out size);
            if (size == 0 || size > 1024)
            {
                return false;
            }

            byte* buffer = stackalloc byte[(int)size];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, size, out _))
            {
                return false;
            }

            TOKEN_USER* user = (TOKEN_USER*)buffer;
            PWSTR text = default;
            if (!PInvoke.ConvertSidToStringSid(user->User.Sid, &text) || text.Value is null)
            {
                return false;
            }

            try
            {
                sid = text.ToString();
                return !string.IsNullOrWhiteSpace(sid);
            }
            finally
            {
                PInvoke.LocalFree((HLOCAL)(nint)text.Value);
            }
        }
    }

    private static async Task WriteFrameAsync(
        PipeStream pipe,
        CredentialProviderProtocol.MessageType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        byte[] header = CredentialProviderProtocol.WriteHeader(type, payload.Length);
        await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CredentialProviderProtocol.Frame> ReadFrameAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[CredentialProviderProtocol.HeaderSize];
        await ReadExactAsync(pipe, header, cancellationToken).ConfigureAwait(false);
        if (!CredentialProviderProtocol.TryReadHeader(header, out CredentialProviderProtocol.MessageType type, out int length, out string? error))
        {
            throw new InvalidDataException(error);
        }

        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
        }

        return new CredentialProviderProtocol.Frame(type, payload);
    }

    private static async Task ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await pipe.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static async Task WaitUntilDisconnectedAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ClientStillConnected(pipe))
            {
                return;
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static unsafe bool ClientStillConnected(NamedPipeServerStream pipe)
    {
        uint available = 0;
        uint bytesLeft = 0;
        uint read = 0;
        return PInvoke.PeekNamedPipe(
            (HANDLE)pipe.SafePipeHandle.DangerousGetHandle(),
            null,
            0,
            &read,
            &available,
            &bytesLeft);
    }

    private static unsafe bool PeerIsConsoleSession(ConnectedPeer peer)
    {
        uint session = 0;
        if (!PInvoke.ProcessIdToSessionId(peer.ProcessId, &session))
        {
            return false;
        }

        uint console = PInvoke.WTSGetActiveConsoleSessionId();
        return session == console && peer.Hello.SessionId == console;
    }

    private sealed class ConnectedPeer(NamedPipeServerStream pipe, CredentialProviderProtocol.HelloMessage hello, uint processId)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;

        public CredentialProviderProtocol.HelloMessage Hello { get; } = hello;

        public uint ProcessId { get; } = processId;

        public bool Busy { get; set; }
    }
}
