using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Vestigo.Processes;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;

namespace Vestigo.Server.Tests;

public class NetworkDiagnosticsTests
{
    [Fact]
    public void ListProcessConnections_OmitsRemoteAddressesUntilRequested()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-04-05T06:07:08Z");
        FakeConnections source = new(ConnectionQuery.Ok(start, partial: false, truncated: false,
        [
            new ConnectionRecord("tcp", "ipv4", "established", "127.0.0.1", 50000, "203.0.113.9", 443, 42),
            new ConnectionRecord("udp", "ipv4", null, "0.0.0.0", 53, null, null, 42)
        ]));
        ConnectionTools tools = new(source, new VestigoOptions(), NullLogger<ConnectionTools>.Instance);

        OperationResult<ProcessConnectionList> hidden = tools.ListProcessConnections(42, expectedStartTime: start);
        string hiddenJson = JsonSerializer.Serialize(hidden.Value, SessionHelper.Json);
        Assert.True(hidden.Success);
        Assert.Null(hidden.Value!.Connections[0].RemoteAddress);
        Assert.Null(hidden.Value.Connections[0].RemotePort);
        Assert.DoesNotContain("203.0.113.9", hiddenJson, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", hiddenJson, StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", hidden.Value.Connections[0].LocalAddress);
        Assert.Null(hidden.Value.Connections[1].State);

        OperationResult<ProcessConnectionList> shown = tools.ListProcessConnections(42, includeRemoteAddresses: true);
        Assert.Equal("203.0.113.9", shown.Value!.Connections[0].RemoteAddress);
        Assert.Equal(443, shown.Value.Connections[0].RemotePort);
    }

    [Fact]
    public void ListProcessConnections_RejectsPidReuseAndMissingProcesses()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-04-05T06:07:08Z");
        FakeConnections source = new(ConnectionQuery.Ok(start, false, false, []));
        ConnectionTools tools = new(source, new VestigoOptions(), NullLogger<ConnectionTools>.Instance);

        Assert.Equal("process_identity_mismatch", tools.ListProcessConnections(7, expectedStartTime: start.AddMinutes(1)).ErrorCode);
        Assert.Equal("process_identity_mismatch", tools.ListProcessConnections(7, expectedStartTime: start.AddSeconds(1)).ErrorCode);
        Assert.True(tools.ListProcessConnections(7, expectedStartTime: start).Success);
        source.Result = ConnectionQuery.Ok(null, false, false, []);
        Assert.Equal("process_identity_mismatch", tools.ListProcessConnections(7, expectedStartTime: start).ErrorCode);
        source.Result = ConnectionQuery.Fail("process_not_found", "The process is not running.");
        OperationResult<ProcessConnectionList> missing = tools.ListProcessConnections(7);
        Assert.Equal("process_not_found", missing.ErrorCode);
        Assert.DoesNotContain("203.0.113.9", missing.Error, StringComparison.Ordinal);
        Assert.Equal("invalid_request", tools.ListProcessConnections(0).ErrorCode);
    }

    [Fact]
    public void ListProcessConnections_OnThisHost_RequiresWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ConnectionQuery query = new WindowsProcessConnectionSource().Query(1, 10);
        Assert.Equal("unsupported", query.ErrorCode);
    }

    [Fact]
    public void Port_ConvertsNetworkOrderAndLeavesZeroUnknown()
    {
        Assert.Equal(80, IpHelperConnections.Port(0x5000));
        Assert.Equal(443, IpHelperConnections.Port(0xBB01));
        Assert.Null(IpHelperConnections.Port(0));
    }

    [Fact]
    public void Tcp4Parse_ExactLimitIsNotTruncated()
    {
        const int pid = 42;
        (IReadOnlyList<ConnectionRecord> rows, bool truncated) = IpHelperConnections.ParseTcp4(IpHelperConnections.SampleTcp4Table(pid, 2), pid, 2);
        Assert.Equal(2, rows.Count);
        Assert.False(truncated);
        Assert.All(rows, row => Assert.Equal(pid, row.OwningPid));

        (IReadOnlyList<ConnectionRecord> full, bool cut) = IpHelperConnections.ParseTcp4(IpHelperConnections.SampleTcp4Table(pid, 2), pid, 0);
        Assert.Empty(full);
        Assert.True(cut);

        (IReadOnlyList<ConnectionRecord> extra, bool overflow) = IpHelperConnections.ParseTcp4(IpHelperConnections.SampleTcp4Table(pid, 3), pid, 2);
        Assert.Equal(2, extra.Count);
        Assert.True(overflow);
    }

    [Fact]
    public async Task TestEndpoint_StaysOffUntilTheGateAndAllowListBothMatch()
    {
        FakeProbe probe = new();
        EndpointProbeTools gated = new(probe, new VestigoOptions { Diagnostics = { AllowedProbeDestinations = ["example.com"] } }, NullLogger<EndpointProbeTools>.Instance);
        Assert.Equal("network_probes_disabled", (await gated.TestEndpoint("example.com", 443)).ErrorCode);
        Assert.Equal(0, probe.Calls);

        VestigoOptions options = new() { Diagnostics = { AllowNetworkProbes = true, AllowedProbeDestinations = ["example.com:443"] } };
        EndpointProbeTools tools = new(probe, options, NullLogger<EndpointProbeTools>.Instance);
        OperationResult<EndpointProbeResult> other = await tools.TestEndpoint("other.example", 443);
        Assert.Equal("destination_not_allowed", other.ErrorCode);
        Assert.Equal("destination_not_allowed", (await tools.TestEndpoint("example.com", 80)).ErrorCode);
        Assert.Equal("destination_not_allowed", (await tools.TestEndpoint("other.example", 3478, protocol: "udp")).ErrorCode);
        Assert.Equal(0, probe.Calls);
        Assert.DoesNotContain("other.example", other.Error, StringComparison.Ordinal);

        OperationResult<EndpointProbeResult> allowed = await tools.TestEndpoint("Example.COM.", 443, includeResolvedAddress: false);
        Assert.True(allowed.Success);
        Assert.Equal(1, probe.Calls);
        Assert.Null(allowed.Value!.Answers[0].Address);
        Assert.Null(allowed.Value.Tcp[0].Address);
        string json = JsonSerializer.Serialize(allowed.Value, SessionHelper.Json);
        Assert.DoesNotContain("203.0.113.10", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestEndpoint_DoesNotCallTheProbeAfterCancellation()
    {
        FakeProbe probe = new();
        VestigoOptions options = new() { Diagnostics = { AllowNetworkProbes = true, AllowedProbeDestinations = ["example.com"] } };
        EndpointProbeTools tools = new(probe, options, NullLogger<EndpointProbeTools>.Instance);
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.TestEndpoint("example.com", 443, cancellationToken: source.Token));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void AllowedProbeDestinations_RejectsWildcardsAndRanges()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { AllowedProbeDestinations = ["*.example.com"] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { AllowedProbeDestinations = ["10.0.0.0/8"] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxProcessConnections = 0 } }.Validate());
        new VestigoOptions { Diagnostics = { AllowedProbeDestinations = ["example.com", "example.com:443", "192.0.2.10", "[2001:db8::1]:443"] } }.Validate();
    }

    [Fact]
    public async Task SocketProbe_ConnectsToLoopbackAndRejectsAnUntrustedCertificate()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task accepted = AcceptAsync(listener);
        SocketEndpointProbe probe = new(new VestigoOptions());

        EndpointProbeResult tcp = await probe.ProbeAsync("127.0.0.1", port, "tcp", TimeSpan.FromSeconds(3), includeResolvedAddress: true, stun: false, CancellationToken.None);
        await accepted;
        Assert.True(tcp.Tcp[0].Connected);
        Assert.Equal("127.0.0.1", tcp.Tcp[0].Address);
        Assert.Null(tcp.Tls);
        Assert.DoesNotContain(".", tcp.Tcp[0].Error ?? "", StringComparison.Ordinal);

        using X509Certificate2 certificate = TestCertificate();
        using TcpListener tlsListener = new(IPAddress.Loopback, 0);
        tlsListener.Start();
        int tlsPort = ((IPEndPoint)tlsListener.LocalEndpoint).Port;
        Task tlsAccepted = AcceptTlsAsync(tlsListener, certificate);
        EndpointProbeResult tls = await probe.ProbeAsync("127.0.0.1", tlsPort, "tls", TimeSpan.FromSeconds(5), includeResolvedAddress: false, stun: false, CancellationToken.None);
        await tlsAccepted;
        Assert.NotNull(tls.Tls);
        Assert.False(tls.Tls!.Authenticated);
        Assert.Null(tls.Tcp[0].Address);
        Assert.DoesNotContain("127.0.0.1", tls.Tcp[0].Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void StunBinding_MatchesTheIpv4TestVector()
    {
        byte[] transaction = Convert.FromHexString("b7e7a701bc34d686fa87dfae");
        byte[] request = StunBinding.CreateRequest(transaction);
        Assert.Equal(Convert.FromHexString("000100002112a442b7e7a701bc34d686fa87dfae"), request);

        byte[] success = StunBinding.CreateSuccess(transaction, IPAddress.Parse("192.0.2.1"), 32853);
        Assert.Equal(Convert.FromHexString("0101000c2112a442b7e7a701bc34d686fa87dfae002000080001a147e112a643"), success);
        StunDecode decoded = StunBinding.Read(success, transaction);
        Assert.True(decoded.Success);
        Assert.Equal("192.0.2.1", decoded.MappedAddress!.ToString());
        Assert.Equal(32853, decoded.MappedPort);
        Assert.True(decoded.Reflexive);
        Assert.False(StunBinding.Read(success, new byte[12]).Success);

        byte[] signed = StunBinding.WithFingerprint(success);
        Assert.True(StunBinding.Read(signed, transaction).Success);
        signed[^1] ^= 0xFF;
        Assert.False(StunBinding.Read(signed, transaction).Success);

        byte[] mapped = (byte[])success.Clone();
        mapped[20] = 0x00;
        mapped[21] = 0x01;
        ushort xport = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(mapped.AsSpan(26));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(mapped.AsSpan(26), (ushort)(xport ^ 0x2112));
        uint xaddr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mapped.AsSpan(28));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mapped.AsSpan(28), xaddr ^ 0x2112A442);
        StunDecode plain = StunBinding.Read(mapped, transaction);
        Assert.True(plain.Success);
        Assert.False(plain.Reflexive);
        Assert.Equal("192.0.2.1", plain.MappedAddress!.ToString());
        Assert.Equal(32853, plain.MappedPort);

        byte[] bare = new byte[StunBinding.HeaderLength];
        success.AsSpan(0, StunBinding.HeaderLength).CopyTo(bare);
        bare[2] = 0;
        bare[3] = 0;
        StunDecode addressless = StunBinding.Read(bare, transaction);
        Assert.True(addressless.Success);
        Assert.Null(addressless.MappedAddress);
    }

    [Fact]
    public void StunBinding_RoundTripsAnIpv6MappedAddressAndRejectsAnUnknownAttribute()
    {
        byte[] transaction = new byte[12];
        transaction[0] = 9;
        IPAddress address = IPAddress.Parse("2001:db8::9");
        byte[] success = StunBinding.CreateSuccess(transaction, address, 3478);
        StunDecode decoded = StunBinding.Read(success, transaction);
        Assert.Equal(address, decoded.MappedAddress);
        Assert.Equal(3478, decoded.MappedPort);
        Assert.True(decoded.Reflexive);

        byte[] unknown = new byte[success.Length + 4];
        success.AsSpan(0, 20).CopyTo(unknown);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(unknown.AsSpan(2), (ushort)(success.Length - 20 + 4));
        success.AsSpan(20).CopyTo(unknown.AsSpan(20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(unknown.AsSpan(unknown.Length - 4), 0x0007);
        Assert.False(StunBinding.Read(unknown, transaction).Success);
        Assert.Contains("does not prove the peer is reachable", UdpObservation.SentDoesNotProveReachability, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestEndpoint_RefusesQuicAndStunOnTcpWithoutSending()
    {
        FakeProbe probe = new();
        VestigoOptions options = new() { Diagnostics = { AllowNetworkProbes = true, AllowedProbeDestinations = ["example.com"], MaxUdpPayloadBytes = 8 } };
        EndpointProbeTools tools = new(probe, options, NullLogger<EndpointProbeTools>.Instance);

        Assert.Equal("protocol_unsupported", (await tools.TestEndpoint("example.com", 443, protocol: "quic")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.TestEndpoint("example.com", 443, protocol: "tcp", stun: true)).ErrorCode);
        Assert.Equal("invalid_request", (await tools.TestEndpoint("example.com", 3478, protocol: "udp", stun: true)).ErrorCode);
        Assert.Equal(0, probe.Calls);
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxUdpPayloadBytes = 0 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxUdpProbeDuration = TimeSpan.FromSeconds(30) } }.Validate());
    }

    [Fact]
    public async Task SocketProbe_SendsOneUdpDatagramAndDoesNotTreatSilenceAsABlock()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        Task<UdpReceiveResult> received = server.ReceiveAsync();
        SocketEndpointProbe probe = new(new VestigoOptions());

        EndpointProbeResult sent = await probe.ProbeAsync("127.0.0.1", port, "udp", TimeSpan.FromSeconds(2), includeResolvedAddress: false, stun: false, CancellationToken.None);
        UdpReceiveResult packet = await received.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("sent", sent.Udp!.Outcome);
        Assert.Equal(1, sent.Udp.BytesSent);
        Assert.Equal(0, sent.Udp.BytesReceived);
        Assert.Null(sent.Udp.Stun);
        Assert.Equal(UdpObservation.SentDoesNotProveReachability, sent.Udp.Reachability);
        Assert.Equal(new byte[] { 0x00 }, packet.Buffer);

        EndpointProbeResult silent = await probe.ProbeAsync("127.0.0.1", port, "udp", TimeSpan.FromMilliseconds(200), includeResolvedAddress: false, stun: true, CancellationToken.None);
        Assert.Equal("sent", silent.Udp!.Outcome);
        Assert.False(silent.Udp.Stun!.Responded);
        Assert.Equal(UdpObservation.StunSilent, silent.Udp.Reachability);
        Assert.Null(silent.Udp.Stun.MappedAddress);
        string json = JsonSerializer.Serialize(silent, SessionHelper.Json);
        Assert.DoesNotContain("blockedByFirewall", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SocketProbe_ReadsAMatchingStunBindingResponse()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        Task responder = ReplyWithMappedAddress(server);
        SocketEndpointProbe probe = new(new VestigoOptions());

        EndpointProbeResult result = await probe.ProbeAsync("127.0.0.1", port, "udp", TimeSpan.FromSeconds(3), includeResolvedAddress: false, stun: true, CancellationToken.None);
        await responder.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("sent", result.Udp!.Outcome);
        Assert.Equal(StunBinding.HeaderLength, result.Udp.BytesSent);
        Assert.True(result.Udp.Stun!.Responded);
        Assert.True(result.Udp.Stun.Reflexive);
        Assert.Equal("203.0.113.8", result.Udp.Stun.MappedAddress);
        Assert.Equal(40000, result.Udp.Stun.MappedPort);
        Assert.Equal(UdpObservation.StunAnswered, result.Udp.Reachability);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task SocketProbe_IgnoresABindingSuccessThatHasNoMappedAddress()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        Task responder = ReplyAfterAnAddresslessSuccess(server);
        SocketEndpointProbe probe = new(new VestigoOptions());

        EndpointProbeResult result = await probe.ProbeAsync("127.0.0.1", port, "udp", TimeSpan.FromSeconds(3), includeResolvedAddress: false, stun: true, CancellationToken.None);
        await responder.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Udp!.Stun!.Responded);
        Assert.Equal("203.0.113.8", result.Udp.Stun.MappedAddress);
        Assert.Equal(40000, result.Udp.Stun.MappedPort);
        Assert.False(result.TimedOut);
    }

    private static async Task ReplyWithMappedAddress(UdpClient server)
    {
        UdpReceiveResult incoming = await server.ReceiveAsync();
        byte[] response = StunBinding.CreateSuccess(incoming.Buffer.AsSpan(8, 12), IPAddress.Parse("203.0.113.8"), 40000);
        await server.SendAsync(response, response.Length, incoming.RemoteEndPoint);
    }

    private static async Task ReplyAfterAnAddresslessSuccess(UdpClient server)
    {
        UdpReceiveResult incoming = await server.ReceiveAsync();
        byte[] transaction = incoming.Buffer.AsSpan(8, StunBinding.TransactionLength).ToArray();
        byte[] bare = new byte[StunBinding.HeaderLength];
        bare[0] = 0x01;
        bare[1] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bare.AsSpan(4), 0x2112A442);
        transaction.CopyTo(bare.AsSpan(8));
        await server.SendAsync(bare, bare.Length, incoming.RemoteEndPoint);
        byte[] response = StunBinding.CreateSuccess(transaction, IPAddress.Parse("203.0.113.8"), 40000);
        await server.SendAsync(response, response.Length, incoming.RemoteEndPoint);
    }

    private static async Task AcceptAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        client.Close();
    }

    private static async Task AcceptTlsAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        await using SslStream ssl = new(stream);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
        }
    }

    private static X509Certificate2 TestCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=vestigo-probe-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FakeConnections(ConnectionQuery result) : IProcessConnectionSource
    {
        public ConnectionQuery Result { get; set; } = result;

        public ConnectionQuery Query(int pid, int limit) => Result;
    }

    private sealed class FakeProbe : IEndpointProbe
    {
        public int Calls { get; private set; }

        public Task<EndpointProbeResult> ProbeAsync(string host, int port, string protocol, TimeSpan timeout, bool includeResolvedAddress, bool stun, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            DnsObservation answer = new("ipv4", "203.0.113.10");
            TcpObservation tcp = new("203.0.113.10", true, null);
            return Task.FromResult(new EndpointProbeResult(host, port, protocol, DateTimeOffset.UtcNow, false, null, [answer], [tcp], null));
        }
    }
}
