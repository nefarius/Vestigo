using System.IO.Pipes;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class CredentialProviderHostTests
{
    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsSubmitted_AndPreservesPeer_WhenProviderReplies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            Task<CredentialProviderProtocol.Frame> clientTask = Task.Run(async () =>
            {
                CredentialProviderProtocol.Frame dispatch = await CredentialProviderHost.ReadFrameAsync(
                    clientSide,
                    ct);
                byte[] reply = CredentialProviderProtocol.EncodeStatus(
                    new CredentialProviderProtocol.StatusMessage(
                        CredentialProviderProtocol.ProviderStatus.Submitted,
                        0,
                        null));
                await CredentialProviderHost.WriteFrameAsync(
                    clientSide,
                    CredentialProviderProtocol.MessageType.Status,
                    reply,
                    ct);
                return dispatch;
            }, ct);

            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                "TESTBOX",
                "pw".ToCharArray(),
                "S-1-5-21-1-2-3-500",
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                ct);

            CredentialProviderProtocol.Frame received = await clientTask;
            Assert.True(result.Success);
            Assert.Equal("Submitted", result.Value);
            Assert.False(dropPeer);
            Assert.Equal(CredentialProviderProtocol.MessageType.Dispatch, received.Type);
            byte[] expectedPayload = CredentialProviderProtocol.EncodeDispatch(
                "Administrator",
                "TESTBOX",
                "pw".ToCharArray(),
                "S-1-5-21-1-2-3-500");
            Assert.Equal(expectedPayload, received.Payload);
        }
    }

    [Fact]
    public async Task ExchangeDispatchAsync_MapsProviderFailureAndCancel_WithoutDroppingPeer()
    {
        await AssertStatusMappingAsync(
            new CredentialProviderProtocol.StatusMessage(
                CredentialProviderProtocol.ProviderStatus.Failed,
                1326,
                "sid_mismatch"),
            expectedErrorCode: "logon_failed",
            expectedMessageFragment: "sid_mismatch");

        await AssertStatusMappingAsync(
            new CredentialProviderProtocol.StatusMessage(
                CredentialProviderProtocol.ProviderStatus.Cancelled,
                0,
                "User dismissed prompt."),
            expectedErrorCode: "cancelled",
            expectedMessageFragment: "User dismissed prompt.");
    }

    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsProtocolError_AndDropsPeer_OnUnexpectedFrameType()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            Task clientTask = Task.Run(async () =>
            {
                _ = await CredentialProviderHost.ReadFrameAsync(clientSide, ct);
                await CredentialProviderHost.WriteFrameAsync(
                    clientSide,
                    CredentialProviderProtocol.MessageType.Hello,
                    [],
                    ct);
            }, ct);

            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                ct);

            await clientTask;
            Assert.False(result.Success);
            Assert.Equal("provider_protocol", result.ErrorCode);
            Assert.True(dropPeer);
        }
    }

    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsLogonFailed_AndDropsPeer_WhenProviderTimesOut()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            Task readOnly = Task.Run(async () =>
            {
                _ = await CredentialProviderHost.ReadFrameAsync(clientSide, ct);
            }, ct);

            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromMilliseconds(100),
                NullLogger.Instance,
                ct);

            await readOnly;
            Assert.False(result.Success);
            Assert.Equal("logon_failed", result.ErrorCode);
            Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(dropPeer);
        }
    }

    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsCancelled_AndDropsPeer_WhenCallerCancels()
    {
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(80));
            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromSeconds(10),
                NullLogger.Instance,
                cts.Token);

            Assert.False(result.Success);
            Assert.Equal("cancelled", result.ErrorCode);
            Assert.True(dropPeer);
        }
    }

    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsNotConnected_AndDropsPeer_WhenStreamClosesOrDisposes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        {
            clientSide.Dispose();
            (OperationResult<string> eofResult, bool eofDrop) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                ct);

            Assert.False(eofResult.Success);
            Assert.Equal("logonui_not_connected", eofResult.ErrorCode);
            Assert.True(eofDrop);
        }

        MemoryStream disposedStream = new();
        disposedStream.Dispose();
        (OperationResult<string> disposedResult, bool disposedDrop) = await CredentialProviderHost.ExchangeDispatchAsync(
            disposedStream,
            "Administrator",
            null,
            "pw".ToCharArray(),
            null,
            TimeSpan.FromSeconds(5),
            NullLogger.Instance,
            ct);

        Assert.False(disposedResult.Success);
        Assert.Equal("logonui_not_connected", disposedResult.ErrorCode);
        Assert.True(disposedDrop);
    }

    [Fact]
    public async Task ExchangeDispatchAsync_ReturnsProviderProtocol_AndDropsPeer_OnInvalidHeader()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            Task clientTask = Task.Run(async () =>
            {
                _ = await CredentialProviderHost.ReadFrameAsync(clientSide, ct);
                await clientSide.WriteAsync(new byte[CredentialProviderProtocol.HeaderSize], ct);
            }, ct);

            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                ct);

            await clientTask;
            Assert.False(result.Success);
            Assert.Equal("provider_protocol", result.ErrorCode);
            Assert.True(dropPeer);
        }
    }

    [Fact]
    public void ClientStillConnected_ReturnsFalse_WhenPipeIsDisposed()
    {
        string pipeName = $"Vestigo.Test.{Guid.NewGuid():N}";
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        pipe.Dispose();

        Assert.False(CredentialProviderHost.ClientStillConnected(pipe));
    }

    [Fact]
    public void ResolveUserSid_HandlesBlankUnmappedAndWellKnownAccounts()
    {
        Assert.Null(CredentialProviderHost.ResolveUserSid("", null));
        Assert.Null(CredentialProviderHost.ResolveUserSid("   ", "LOCAL"));
        Assert.Null(CredentialProviderHost.ResolveUserSid($"vestigo-nonexistent-{Guid.NewGuid():N}", null));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("S-1-5-18", CredentialProviderHost.ResolveUserSid("SYSTEM", "NT AUTHORITY"));
        }
    }

    [Fact]
    public async Task DispatchToStreamAsync_TimesOutSlowSidResolution_WithoutWritingCredentials()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ManualResetEventSlim releaseLookup = new(false);
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            try
            {
                (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.DispatchToStreamAsync(
                    serverSide,
                    new CredentialProviderProtocol.HelloMessage(CredentialProviderProtocol.UsageScenario.Logon, 1, []),
                    "Administrator",
                    "CORP",
                    "pw".ToCharArray(),
                    expectedSid: null,
                    TimeSpan.FromMilliseconds(100),
                    (_, _) =>
                    {
                        releaseLookup.Wait(TimeSpan.FromSeconds(5));
                        return "S-1-5-21-1-2-3-500";
                    },
                    NullLogger.Instance,
                    ct);

                Assert.False(result.Success);
                Assert.Equal("logon_failed", result.ErrorCode);
                Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(dropPeer);
                Assert.Equal(0, serverSide.WritesCount);
            }
            finally
            {
                releaseLookup.Set();
            }
        }
    }

    [Fact]
    public async Task DispatchToStreamAsync_CancelsSlowSidResolution_WithoutWritingCredentials()
    {
        using ManualResetEventSlim releaseLookup = new(false);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(80));
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            try
            {
                (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.DispatchToStreamAsync(
                    serverSide,
                    new CredentialProviderProtocol.HelloMessage(CredentialProviderProtocol.UsageScenario.Logon, 1, []),
                    "Administrator",
                    "CORP",
                    "pw".ToCharArray(),
                    expectedSid: null,
                    TimeSpan.FromSeconds(10),
                    (_, _) =>
                    {
                        releaseLookup.Wait(TimeSpan.FromSeconds(5));
                        return "S-1-5-21-1-2-3-500";
                    },
                    NullLogger.Instance,
                    cts.Token);

                Assert.False(result.Success);
                Assert.Equal("cancelled", result.ErrorCode);
                Assert.False(dropPeer);
                Assert.Equal(0, serverSide.WritesCount);
            }
            finally
            {
                releaseLookup.Set();
            }
        }
    }

    [Fact]
    public async Task DispatchToStreamAsync_RejectsUnlockSidMismatch_FromResolvedSid_WithoutWritingCredentials()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.DispatchToStreamAsync(
                serverSide,
                new CredentialProviderProtocol.HelloMessage(
                    CredentialProviderProtocol.UsageScenario.Unlock,
                    1,
                    ["S-1-5-21-1-2-3-1001"]),
                "Administrator",
                "TESTBOX",
                "pw".ToCharArray(),
                expectedSid: null,
                TimeSpan.FromSeconds(5),
                (_, _) => "S-1-5-21-1-2-3-500",
                NullLogger.Instance,
                ct);

            Assert.False(result.Success);
            Assert.Equal("sid_mismatch", result.ErrorCode);
            Assert.False(dropPeer);
            Assert.Equal(0, serverSide.WritesCount);
        }
    }

    private static async Task AssertStatusMappingAsync(
        CredentialProviderProtocol.StatusMessage status,
        string expectedErrorCode,
        string expectedMessageFragment)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DuplexTestStream serverSide, DuplexTestStream clientSide) = DuplexTestStream.CreatePair();
        using (serverSide)
        using (clientSide)
        {
            Task clientTask = Task.Run(async () =>
            {
                _ = await CredentialProviderHost.ReadFrameAsync(clientSide, ct);
                byte[] reply = CredentialProviderProtocol.EncodeStatus(status);
                await CredentialProviderHost.WriteFrameAsync(
                    clientSide,
                    CredentialProviderProtocol.MessageType.Status,
                    reply,
                    ct);
            }, ct);

            (OperationResult<string> result, bool dropPeer) = await CredentialProviderHost.ExchangeDispatchAsync(
                serverSide,
                "Administrator",
                null,
                "pw".ToCharArray(),
                null,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                ct);

            await clientTask;
            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            Assert.Contains(expectedMessageFragment, result.Error, StringComparison.Ordinal);
            Assert.False(dropPeer);
        }
    }

    private sealed class DuplexTestStream : Stream
    {
        private readonly ChannelReader<byte[]> _incoming;
        private readonly ChannelWriter<byte[]> _outgoing;
        private byte[] _pending = [];
        private int _pendingOffset;
        private bool _disposed;
        private int _writesCount;

        public int WritesCount => Volatile.Read(ref _writesCount);

        private DuplexTestStream(ChannelReader<byte[]> incoming, ChannelWriter<byte[]> outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;
        }

        public static (DuplexTestStream Left, DuplexTestStream Right) CreatePair()
        {
            Channel<byte[]> aToB = Channel.CreateUnbounded<byte[]>();
            Channel<byte[]> bToA = Channel.CreateUnbounded<byte[]>();
            return (new DuplexTestStream(bToA.Reader, aToB.Writer), new DuplexTestStream(aToB.Reader, bToA.Writer));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingOffset >= _pending.Length)
            {
                if (!await _incoming.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (!_incoming.TryRead(out byte[]? chunk))
                {
                    return 0;
                }

                _pending = chunk;
                _pendingOffset = 0;
            }

            int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
            _pendingOffset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _writesCount);
            byte[] copy = buffer.ToArray();
            if (!_outgoing.TryWrite(copy))
            {
                throw new IOException("Peer stream is closed.");
            }

            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _outgoing.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
