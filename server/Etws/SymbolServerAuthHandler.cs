using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.Etws;

/// <summary>
///     Adds symbol-server credentials only to the configured origin and follows redirects without
///     forwarding those credentials across origins.
/// </summary>
public sealed class SymbolServerAuthHandler(VestigoOptions options) : DelegatingHandler
{
    public const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApplyCredentials(request);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        int hops = 0;
        while (IsRedirect(response.StatusCode) && hops < MaxRedirects)
        {
            Uri? location = response.Headers.Location;
            if (location is null)
            {
                break;
            }

            Uri next = location.IsAbsoluteUri
                ? location
                : new Uri(request.RequestUri ?? throw new InvalidOperationException("Request URI is required."), location);
            bool sameOrigin = SameOrigin(request.RequestUri, next);
            HttpRequestMessage follow = new(HttpMethod.Get, next);
            if (sameOrigin)
            {
                ApplyCredentials(follow);
            }

            response.Dispose();
            request = follow;
            response = await base.SendAsync(follow, cancellationToken).ConfigureAwait(false);
            hops++;
        }

        return response;
    }

    internal void ApplyCredentials(HttpRequestMessage request)
    {
        if (!options.SymbolServerAuth.IsConfigured ||
            string.IsNullOrWhiteSpace(options.SymbolServer) ||
            !Uri.TryCreate(options.SymbolServer, UriKind.Absolute, out Uri? server) ||
            request.RequestUri is null ||
            !SameOrigin(request.RequestUri, server))
        {
            return;
        }

        SymbolServerAuthOptions auth = options.SymbolServerAuth;
        switch (auth.Scheme)
        {
            case SymbolServerAuthScheme.Basic:
                string pair = (auth.Username ?? "") + ":" + (auth.ResolvedSecret ?? "");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                break;
            case SymbolServerAuthScheme.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.ResolvedSecret);
                break;
            case SymbolServerAuthScheme.Header:
                if (!string.IsNullOrWhiteSpace(auth.HeaderName))
                {
                    request.Headers.Remove(auth.HeaderName);
                    request.Headers.TryAddWithoutValidation(auth.HeaderName, auth.ResolvedSecret);
                }

                break;
        }
    }

    internal static bool SameOrigin(Uri? left, Uri? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
