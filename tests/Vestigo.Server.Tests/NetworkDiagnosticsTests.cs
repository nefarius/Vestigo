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
        SocketEndpointProbe probe = new();

        EndpointProbeResult tcp = await probe.ProbeAsync("127.0.0.1", port, "tcp", TimeSpan.FromSeconds(3), includeResolvedAddress: true, CancellationToken.None);
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
        EndpointProbeResult tls = await probe.ProbeAsync("127.0.0.1", tlsPort, "tls", TimeSpan.FromSeconds(5), includeResolvedAddress: false, CancellationToken.None);
        await tlsAccepted;
        Assert.NotNull(tls.Tls);
        Assert.False(tls.Tls!.Authenticated);
        Assert.Null(tls.Tcp[0].Address);
        Assert.DoesNotContain("127.0.0.1", tls.Tcp[0].Error ?? "", StringComparison.Ordinal);
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

        public Task<EndpointProbeResult> ProbeAsync(string host, int port, string protocol, TimeSpan timeout, bool includeResolvedAddress, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            DnsObservation answer = new("ipv4", "203.0.113.10");
            TcpObservation tcp = new("203.0.113.10", true, null);
            return Task.FromResult(new EndpointProbeResult(host, port, protocol, DateTimeOffset.UtcNow, false, null, [answer], [tcp], null));
        }
    }
}
