using System.Net;

namespace Vestigo.Hosting;

/// <summary>Gate both the page and its interactive transport; forwarded addresses are never trusted.</summary>
internal sealed class LocalConfigurationAccess(RequestDelegate next)
{
    internal static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/config") || path.StartsWithSegments("/_blazor") ||
        path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content");

    internal static bool IsAllowed(HttpContext context)
    {
        HttpRequest request = context.Request;
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return false;
        string host = request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            (!IPAddress.TryParse(host, out IPAddress? address) || !IPAddress.IsLoopback(address)))
            return false;
        int port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        if (port != context.Connection.LocalPort) return false;
        if (request.Headers.ContainsKey("Forwarded") || request.Headers.ContainsKey("X-Forwarded-For") ||
            request.Headers.ContainsKey("X-Forwarded-Host")) return false;
        if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        if (request.Headers.TryGetValue("Origin", out var origin) &&
            !string.Equals(origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsProtectedPath(context.Request.Path))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!IsAllowed(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(context);
    }
}
