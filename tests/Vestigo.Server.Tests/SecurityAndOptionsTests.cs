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
}
