using System.Net.Http.Headers;

using Vestigo.Configuration;

namespace Vestigo.Desktop;

/// <summary>Serves one downscaled screenshot preview. The ticket is the only credential; an API token is not accepted.</summary>
public static class ScreenshotShareEndpoint
{
    public static void Map(WebApplication app, VestigoOptions options)
    {
        IEndpointConventionBuilder get = app.MapGet("/desktop/shares/{id}", GetAsync);
        if (options.AuthenticationEnabled)
        {
            get.AllowAnonymous();
        }
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, ScreenshotShareStore shares, string? ticket = null)
    {
        ScreenshotShareStore.Reservation? reservation = shares.TryReserve(id, ticket, out ShareFailure failure);
        if (reservation is null)
        {
            return Unauthorized(failure);
        }

        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "image/png";
            context.Response.ContentLength = reservation.Png.Length;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("inline") { FileNameStar = "vestigo-screenshot.png" }.ToString();
            await context.Response.Body.WriteAsync(reservation.Png, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch
        {
            shares.Release(reservation);
            throw;
        }

        shares.Consume(reservation);
        return Results.Empty;
    }

    private static IResult Unauthorized(ShareFailure failure)
    {
        string message = failure switch
        {
            ShareFailure.Missing => "Provide the share ticket for this screenshot.",
            ShareFailure.Expired => "The share ticket has expired.",
            ShareFailure.Mismatched => "The share ticket does not match this screenshot.",
            ShareFailure.Replayed => "The share ticket was already used.",
            _ => "The share ticket is unknown."
        };
        return Results.Json(new { error = "invalid_ticket", message }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
