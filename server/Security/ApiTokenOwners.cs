using System.Security.Cryptography;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>In-memory opaque owner IDs, never token values or externally exposed token digests.</summary>
public sealed class ApiTokenOwners(VestigoOptions options)
{
    private readonly IReadOnlyDictionary<string, string> _owners = options.ApiTokens
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(Digest, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);

    // Only called after fixed-time token verification succeeded.
    public string GetVerifiedOwner(string token) => _owners[Digest(token)];

    private static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
