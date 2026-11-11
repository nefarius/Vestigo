using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using ModelContextProtocol.AspNetCore;

using Serilog;

using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Etws;
using Vestigo.Hosting;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.Security;

// Presentation decoding runs in this child so a native ETW metadata fault cannot terminate the service.
if (args.Length > 0 && string.Equals(args[0], Vestigo.Etws.PresentationWorker.Flag, StringComparison.Ordinal))
{
    Environment.ExitCode = Vestigo.Etws.PresentationWorker.Run(args);
    return;
}

// Session helper: started by the service inside the console user's session; must not boot the web host.
if (SessionHelper.IsInvocation(args))
{
    Environment.ExitCode = SessionHelper.Run(args);
    return;
}

WebApplicationOptions webOptions = new()
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
IReadOnlyList<string> stateRepairs = StateDirectories.EnsureSecure();

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

ListenEndpoint.RejectConflictingKestrelEndpoints(builder.Configuration);
builder.WebHost.UseUrls(ListenEndpoint.Url(vestigo.ListenAddress, vestigo.ListenPort));
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
builder.Services.AddSingleton<PathPolicy>();
builder.Services.AddSingleton<IVolumeEnumerator, DriveInfoVolumeEnumerator>();
builder.Services.AddSingleton<VolumeRootSource>();
builder.Services.AddSingleton<RootPolicy>();
builder.Services.AddDevices();

ModuleRegistry.ValidateConfiguration(vestigo);
IReadOnlyList<VestigoModule> modules = ModuleRegistry.Enabled(vestigo);
foreach (VestigoModule module in modules)
{
    module.ConfigureServices(builder.Services, vestigo);
}

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .WithTools(modules.SelectMany(module => module.ToolTypes));

WebApplication app = builder.Build();
Log.Information("Vestigo modules enabled: {Modules}", string.Join(", ", modules.Select(module => module.Name)));
foreach (string repair in stateRepairs)
{
    app.Logger.LogWarning("{StateDirectoryRepair}", repair);
}

IReadOnlyList<RootDefinition> volumeRoots = app.Services.GetRequiredService<VolumeRootSource>().Current;
if (volumeRoots.Count > 0)
{
    app.Logger.LogInformation(
        "Vestigo volume roots ({Mode}): {VolumeRoots}",
        vestigo.VolumeRoots,
        string.Join(", ", volumeRoots.Select(root => $"{root.Name}={root.Path}")));
}

if (vestigo.GetExposureWarning() is { } exposureWarning)
{
    app.Logger.LogWarning("{ExposureWarning}", exposureWarning);
}

if (vestigo.GetVolumeRootWarning() is { } volumeRootWarning)
{
    app.Logger.LogWarning("{VolumeRootWarning}", volumeRootWarning);
}

if (vestigo.GetTracesRootOverrideWarning() is { } tracesRootWarning)
{
    app.Logger.LogWarning("{TracesRootWarning}", tracesRootWarning);
}

if (vestigo.GetSymbolServerQueryWarning() is { } symbolQueryWarning)
{
    app.Logger.LogWarning("{SymbolServerQueryWarning}", symbolQueryWarning);
}
if (ModuleRegistry.GetPrivilegedModuleWarning(vestigo) is { } privilegedWarning)
{
    app.Logger.LogWarning("{PrivilegedWarning}", privilegedWarning);
}
foreach (VestigoModule module in modules)
{
    module.MapEndpoints(app, vestigo);
}

if (vestigo.AuthenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapHealthChecks("/health", new HealthCheckOptions { AllowCachingResponses = false }).AllowAnonymous();
app.MapLandingPage();
IEndpointConventionBuilder mcp = app.MapMcp("/mcp");
if (vestigo.AuthenticationEnabled)
{
    mcp.RequireAuthorization();
}

await app.RunAsync();

/// <summary>Entry point marker used by the test host.</summary>
public partial class Program;

internal static class ListenEndpoint
{
    public static string Url(string address, int port)
    {
        System.Net.IPAddress ip = System.Net.IPAddress.Parse(address);
        string host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ip}]"
            : ip.ToString();
        return $"http://{host}:{port}";
    }

    public static void RejectConflictingKestrelEndpoints(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "Kestrel:Endpoints is not supported. Set Vestigo:ListenAddress and Vestigo:ListenPort.");
        }
    }
}
