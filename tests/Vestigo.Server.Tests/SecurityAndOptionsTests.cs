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
    public void Options_RejectNonIpListenAddress()
    {
        Assert.Throws<InvalidOperationException>(new VestigoOptions { ListenAddress = "localhost" }.Validate);
        Assert.Throws<InvalidOperationException>(new VestigoOptions { ListenAddress = "" }.Validate);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.20")]
    [InlineData("fe80::1")]
    public void Options_AcceptNonLoopbackListenAddress(string address)
    {
        VestigoOptions options = new() { ListenAddress = address };

        options.Validate();

        Assert.True(options.IsBoundBeyondLoopback);
    }

    [Theory]
    [InlineData("0.0.0.0", false, true)]
    [InlineData("::", false, true)]
    [InlineData("0.0.0.0", true, false)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("::1", false, false)]
    public void Options_WarnOnlyWhenExposedWithoutTokens(string address, bool withToken, bool expectWarning)
    {
        VestigoOptions options = new() { ListenAddress = address };
        if (withToken)
        {
            options.ApiTokens.Add("secret-value");
        }

        string? warning = options.GetExposureWarning();

        Assert.Equal(expectWarning, warning is not null);
        if (warning is not null)
        {
            Assert.Contains(address, warning);
            Assert.DoesNotContain("secret-value", warning);
        }
    }

    [Fact]
    public void Options_RejectBlankTokensAndNonPositiveLimits()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { ApiTokens = [""] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { MaxEvents = 0 }.Validate());
    }

    [Theory]
    [InlineData("\\data")]
    [InlineData("C:data")]
    [InlineData("relative")]
    public void Options_RejectNonFullyQualifiedRootPaths(string path)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new VestigoOptions { Roots = [new RootDefinition { Name = "rt", Path = path }] }.Validate());
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
