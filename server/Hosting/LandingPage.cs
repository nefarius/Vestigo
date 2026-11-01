using System.Reflection;

namespace Vestigo.Hosting;

internal static class LandingPage
{
    private const string ResourceName = "Vestigo.Hosting.LandingPage.html";
    private static readonly string Html = ReadEmbeddedHtml();

    public static void MapLandingPage(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static string ReadEmbeddedHtml()
    {
        Assembly assembly = typeof(LandingPage).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
