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
    internal static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromSeconds(25);

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

        try
        {
            if (!PeerIsConsoleSession(peer))
            {
                return OperationResult<string>.Fail(
                    "logonui_not_connected",
                    "LogonUI is not attached to the active console session.");
            }

            (OperationResult<string> result, bool dropPeer) = await DispatchToStreamAsync(
                peer.Pipe,
                peer.Hello,
                userName,
                domain,
                password,
                expectedSid,
                DefaultDispatchTimeout,
                ResolveUserSid,
                _logger,
                cancellationToken).ConfigureAwait(false);
            if (dropPeer)
            {
                Drop(peer);
            }

            return result;
        }
        finally
        {
            peer.Busy = false;
        }
    }

    internal static Task<(OperationResult<string> Result, bool DropPeer)> ExchangeDispatchAsync(
        Stream stream,
        string userName,
        string? domain,
        char[] password,
        string? targetSid,
        TimeSpan timeout,
        ILogger? logger,
        CancellationToken cancellationToken) =>
        DispatchToStreamAsync(
            stream,
            new CredentialProviderProtocol.HelloMessage(CredentialProviderProtocol.UsageScenario.Logon, 0, []),
            userName,
            domain,
            password,
            targetSid,
            timeout,
            static (_, _) => null,
            logger,
            cancellationToken);

    internal static async Task<(OperationResult<string> Result, bool DropPeer)> DispatchToStreamAsync(
        Stream stream,
        CredentialProviderProtocol.HelloMessage hello,
        string userName,
        string? domain,
        char[] password,
        string? expectedSid,
        TimeSpan timeout,
        Func<string, string?, string?> sidResolver,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        byte[] payload = [];
        bool sentDispatch = false;
        using CancellationTokenSource dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dispatchCts.CancelAfter(timeout);
        CancellationToken token = dispatchCts.Token;

        try
        {
            string? targetSid = !string.IsNullOrWhiteSpace(expectedSid)
                ? expectedSid
                : await ResolveUserSidAsync(userName, domain, sidResolver, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (hello.Scenario == CredentialProviderProtocol.UsageScenario.Unlock &&
                !string.IsNullOrWhiteSpace(targetSid) &&
                !hello.UserSids.Contains(targetSid, StringComparer.OrdinalIgnoreCase))
            {
                return (
                    OperationResult<string>.Fail(
                        "sid_mismatch",
                        "The unlock target is not one of the users on the lock screen."),
                    false);
            }

            payload = CredentialProviderProtocol.EncodeDispatch(userName, domain ?? "", password, targetSid);
            sentDispatch = true;
            await WriteFrameAsync(stream, CredentialProviderProtocol.MessageType.Dispatch, payload, token)
                .ConfigureAwait(false);
            CredentialProviderProtocol.Frame frame = await ReadFrameAsync(stream, token).ConfigureAwait(false);
            if (frame.Type != CredentialProviderProtocol.MessageType.Status)
            {
                return (
                    OperationResult<string>.Fail("provider_protocol", "The Credential Provider sent an unexpected frame."),
                    true);
            }

            CredentialProviderProtocol.StatusMessage status = CredentialProviderProtocol.DecodeStatus(frame.Payload);
            OperationResult<string> mapped = status.Status switch
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
            return (mapped, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (OperationResult<string>.Fail("cancelled", "The interactive logon was cancelled."), sentDispatch);
        }
        catch (OperationCanceledException)
        {
            if (sentDispatch)
            {
                logger?.LogWarning("Credential Provider timed out waiting for dispatch reply after {TimeoutSeconds}s.", timeout.TotalSeconds);
                return (
                    OperationResult<string>.Fail("logon_failed", "LogonUI did not respond before the dispatch timed out."),
                    true);
            }

            logger?.LogWarning("Account SID resolution timed out before dispatch after {TimeoutSeconds}s.", timeout.TotalSeconds);
            return (
                OperationResult<string>.Fail("logon_failed", "Account SID resolution timed out before dispatch."),
                false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger?.LogWarning(exception, "Credential Provider pipe closed during dispatch.");
            return (OperationResult<string>.Fail("logonui_not_connected", "LogonUI disconnected during dispatch."), true);
        }
        catch (InvalidDataException exception)
        {
            logger?.LogWarning(exception, "Credential Provider sent an invalid dispatch reply.");
            return (OperationResult<string>.Fail("provider_protocol", exception.Message), true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static async Task<string?> ResolveUserSidAsync(
        string userName,
        string? domain,
        Func<string, string?, string?> resolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Task<string?> lookup = Task.Run(() =>
        {
            try
            {
                return resolver(userName, domain);
            }
            catch (Exception exception) when (exception is IdentityNotMappedException or SystemException or ArgumentException)
            {
                return null;
            }
        }, CancellationToken.None);

        return await lookup.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string? ResolveUserSid(string userName, string? domain)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        try
        {
            NTAccount account;
            if (userName.Contains('\\') || userName.Contains('@'))
            {
                account = new NTAccount(userName);
            }
            else if (!string.IsNullOrWhiteSpace(domain))
            {
                string trimmedDomain = domain.Trim();
                string normalizedDomain = trimmedDomain == "." ? Environment.MachineName : trimmedDomain;
                account = new NTAccount(normalizedDomain, userName);
            }
            else
            {
                account = new NTAccount(userName);
            }

            return account.Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid
                ? sid.Value
                : null;
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or SystemException or ArgumentException)
        {
            return null;
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
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
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
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
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

    internal static async Task WriteFrameAsync(
        Stream stream,
        CredentialProviderProtocol.MessageType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        byte[] header = CredentialProviderProtocol.WriteHeader(type, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CredentialProviderProtocol.Frame> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[CredentialProviderProtocol.HeaderSize];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!CredentialProviderProtocol.TryReadHeader(header, out CredentialProviderProtocol.MessageType type, out int length, out string? error))
        {
            throw new InvalidDataException(error);
        }

        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new CredentialProviderProtocol.Frame(type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
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

    internal static unsafe bool ClientStillConnected(NamedPipeServerStream pipe)
    {
        SafePipeHandle handle;
        try
        {
            handle = pipe.SafePipeHandle;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }

        if (handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            if (!addedRef)
            {
                return false;
            }

            uint available = 0;
            uint bytesLeft = 0;
            uint read = 0;
            return PInvoke.PeekNamedPipe(
                (HANDLE)handle.DangerousGetHandle(),
                null,
                0,
                &read,
                &available,
                &bytesLeft);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
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
