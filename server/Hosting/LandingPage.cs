using System.Reflection;

namespace Vestigo.Hosting;

internal static class LandingPage
{
    private const string ResourceName = "Vestigo.Hosting.LandingPage.html";
    private const string FaviconResourceName = "Vestigo.Hosting.favicon.ico";
    private static readonly string Html = ReadEmbeddedHtml();
    private static readonly byte[] Favicon = ReadEmbeddedBytes(FaviconResourceName);

    public static void MapLandingPage(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapMethods("/favicon.ico", [HttpMethods.Get, HttpMethods.Head], async (HttpContext context) =>
            {
                context.Response.ContentType = "image/x-icon";
                context.Response.ContentLength = Favicon.Length;
                context.Response.Headers.CacheControl = "public, max-age=86400";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.Body.WriteAsync(Favicon, context.RequestAborted);
                }
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static string ReadEmbeddedHtml()
    {
        using Stream stream = OpenResource(ResourceName);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ReadEmbeddedBytes(string name)
    {
        using Stream stream = OpenResource(name);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenResource(string name) =>
        typeof(LandingPage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' was not found.");
}
