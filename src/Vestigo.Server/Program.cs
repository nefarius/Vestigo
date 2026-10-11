using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using ModelContextProtocol.AspNetCore;

using Serilog;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Hosting;
using Vestigo.Security;

WebApplicationOptions webOptions = new()
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
Directory.CreateDirectory(VestigoPaths.LogDirectory);
Directory.CreateDirectory(VestigoPaths.SpoolDirectory);

WebApplicationBuilder builder = WebApplication.CreateBuilder(webOptions);
builder.Configuration.AddJsonFile(VestigoPaths.SettingsFilePath, optional: true, reloadOnChange: false);

VestigoOptions vestigo = builder.Configuration.GetSection(VestigoOptions.SectionName).Get<VestigoOptions>() ?? new VestigoOptions();
vestigo.Validate();

builder.Host.UseSerilog((context, _, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(VestigoPaths.LogDirectory, "vestigo-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Host.UseWindowsService(options => options.ServiceName = "Vestigo");
}

builder.WebHost.UseUrls($"http://{vestigo.ListenAddress}:{vestigo.ListenPort}");
builder.Services.AddSingleton(vestigo);

if (vestigo.AuthenticationEnabled)
{
    builder.Services.AddAuthentication(ApiTokenDefaults.Scheme)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
            ApiTokenDefaults.Scheme,
            null);
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}

builder.Services.AddHealthChecks().AddCheck<VestigoHealthCheck>("vestigo");
builder.Services.AddHttpClient("symbols");
builder.Services.AddSingleton<PathPolicy>();
builder.Services.AddSingleton<SymbolCatalogService>();
builder.Services.AddSingleton<OfflineConversionService>();
builder.Services.AddSingleton<IRealtimeTraceSessionFactory, EtwRealtimeTraceSessionFactory>();
builder.Services.AddSingleton<CaptureManager>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CaptureManager>());

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .WithToolsFromAssembly();

WebApplication app = builder.Build();
if (vestigo.AuthenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapHealthChecks("/health", new HealthCheckOptions { AllowCachingResponses = false }).AllowAnonymous();
IEndpointConventionBuilder mcp = app.MapMcp("/mcp");
if (vestigo.AuthenticationEnabled)
{
    mcp.RequireAuthorization();
}

await app.RunAsync();

/// <summary>Entry point marker used by the test host.</summary>
public partial class Program;
