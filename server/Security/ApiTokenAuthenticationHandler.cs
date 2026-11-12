using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>Authentication scheme name for configured API tokens.</summary>
public static class ApiTokenDefaults
{
    public const string Scheme = "ApiToken";
}

/// <summary>
///     Compares bearer tokens with SHA-256 and a fixed-time equality check.
/// </summary>
public static class ApiTokenVerifier
{
    public static bool Matches(string provided, IEnumerable<string> configuredTokens)
    {
        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        bool match = false;
        foreach (string token in configuredTokens)
        {
            byte[] candidate = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            match |= CryptographicOperations.FixedTimeEquals(providedHash, candidate);
        }

        return match;
    }
}

/// <summary>
///     Authenticates <c>Authorization: Bearer</c> against the configured API tokens.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    VestigoOptions vestigo,
    ApiTokenOwners owners)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string provided = header["Bearer ".Length..].Trim();
        if (provided.Length == 0 || !ApiTokenVerifier.Matches(provided, vestigo.ApiTokens))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API token."));
        }

        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.NameIdentifier, owners.GetVerifiedOwner(provided))],
            ApiTokenDefaults.Scheme);
        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), ApiTokenDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
