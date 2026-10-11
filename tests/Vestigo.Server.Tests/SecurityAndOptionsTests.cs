using Microsoft.Extensions.Configuration;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class SecurityAndOptionsTests
{
    [Fact]
    public void Verifier_MatchesConfiguredTokenAndRejectsOthers()
    {
        string[] tokens = ["alpha-token", "beta-token"];

        Assert.True(ApiTokenVerifier.Matches("beta-token", tokens));
        Assert.False(ApiTokenVerifier.Matches("beta-token-extra", tokens));
        Assert.False(ApiTokenVerifier.Matches("", tokens));
    }

    [Fact]
    public void Options_RejectNonLoopbackListenAddress()
    {
        VestigoOptions options = new() { ListenAddress = "0.0.0.0" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_RejectBlankTokensAndNonPositiveLimits()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { ApiTokens = [""] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { MaxEvents = 0 }.Validate());
    }

    [Fact]
    public void ListenUrl_BracketsIpv6AndKeepsIpv4()
    {
        Assert.Equal("http://[::1]:5098", ListenEndpoint.Url("::1", 5098));
        Assert.Equal("http://127.0.0.1:5098", ListenEndpoint.Url("127.0.0.1", 5098));
    }

    [Fact]
    public void ListenEndpoint_RejectsKestrelEndpointOverrides()
    {
        IConfiguration conflicting = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:80"
            })
            .Build();
        IConfiguration empty = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => ListenEndpoint.RejectConflictingKestrelEndpoints(conflicting));
        ListenEndpoint.RejectConflictingKestrelEndpoints(empty);
    }
}
