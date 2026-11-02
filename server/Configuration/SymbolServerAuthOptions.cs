namespace Vestigo.Configuration;

/// <summary>How Vestigo authenticates to the configured HTTP symbol server.</summary>
public enum SymbolServerAuthScheme
{
    None = 0,
    Basic = 1,
    Bearer = 2,
    Header = 3
}

/// <summary>Admin-only credentials for <c>Vestigo:SymbolServer</c>. Agents never see the secret.</summary>
public sealed class SymbolServerAuthOptions
{
    public SymbolServerAuthScheme Scheme { get; set; } = SymbolServerAuthScheme.None;

    /// <summary>Header name when <see cref="Scheme" /> is <see cref="SymbolServerAuthScheme.Header" />.</summary>
    public string? HeaderName { get; set; }

    /// <summary>User name when <see cref="Scheme" /> is <see cref="SymbolServerAuthScheme.Basic" />.</summary>
    public string? Username { get; set; }

    /// <summary>Inline secret. Mutually exclusive with <see cref="SecretFile" />.</summary>
    public string? Secret { get; set; }

    /// <summary>Absolute path of a file whose entire contents are the secret. Read once at startup.</summary>
    public string? SecretFile { get; set; }

    internal string? ResolvedSecret;

    public bool IsConfigured => Scheme != SymbolServerAuthScheme.None && !string.IsNullOrEmpty(ResolvedSecret);

    public void Validate(string? symbolServer)
    {
        if (!Enum.IsDefined(Scheme))
        {
            throw new InvalidOperationException($"Vestigo:SymbolServerAuth:Scheme '{Scheme}' is not recognised.");
        }

        if (!string.IsNullOrWhiteSpace(Secret) && !string.IsNullOrWhiteSpace(SecretFile))
        {
            throw new InvalidOperationException(
                "Vestigo:SymbolServerAuth cannot set both Secret and SecretFile.");
        }

        if (!string.IsNullOrWhiteSpace(SecretFile) && !Path.IsPathFullyQualified(SecretFile))
        {
            throw new InvalidOperationException("Vestigo:SymbolServerAuth:SecretFile must be an absolute path.");
        }

        if (Scheme == SymbolServerAuthScheme.None)
        {
            if (!string.IsNullOrWhiteSpace(Secret) || !string.IsNullOrWhiteSpace(SecretFile) ||
                !string.IsNullOrWhiteSpace(HeaderName) || !string.IsNullOrWhiteSpace(Username))
            {
                throw new InvalidOperationException(
                    "Vestigo:SymbolServerAuth:Scheme is None, so HeaderName, Username, Secret, and SecretFile must be empty.");
            }

            ResolvedSecret = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(symbolServer))
        {
            throw new InvalidOperationException(
                "Vestigo:SymbolServerAuth requires an explicit Vestigo:SymbolServer.");
        }

        if (Scheme == SymbolServerAuthScheme.Header && string.IsNullOrWhiteSpace(HeaderName))
        {
            throw new InvalidOperationException("Vestigo:SymbolServerAuth:HeaderName is required when Scheme is Header.");
        }

        if (Scheme == SymbolServerAuthScheme.Basic && string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException("Vestigo:SymbolServerAuth:Username is required when Scheme is Basic.");
        }

        if (string.IsNullOrWhiteSpace(Secret) && string.IsNullOrWhiteSpace(SecretFile))
        {
            throw new InvalidOperationException("Vestigo:SymbolServerAuth needs Secret or SecretFile when Scheme is not None.");
        }

        if (!Uri.TryCreate(symbolServer, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Vestigo:SymbolServer must be an absolute http or https URL when authentication is configured.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !IsLoopback(uri))
        {
            throw new InvalidOperationException(
                "Vestigo:SymbolServerAuth requires HTTPS except when the symbol server is loopback.");
        }

        ResolvedSecret = ReadSecret();
        if (string.IsNullOrEmpty(ResolvedSecret))
        {
            throw new InvalidOperationException("Vestigo:SymbolServerAuth secret is empty.");
        }
    }

    internal static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private string ReadSecret()
    {
        if (!string.IsNullOrWhiteSpace(Secret))
        {
            return Secret;
        }

        try
        {
            return File.ReadAllText(SecretFile!).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Vestigo:SymbolServerAuth:SecretFile could not be read: {ex.GetType().Name}.");
        }
    }
}
