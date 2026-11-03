using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.Security;
using Vestigo.Security.Signatures;

namespace Vestigo.Files;

/// <summary>File browsing, transfer and replacement inside configured roots.</summary>
public sealed class FilesModule : VestigoModule
{
    public override string Name => "Files";

    public override string Description => "Root-confined file tools plus PUT/GET transfer endpoints that accept the API token or a one-time ticket.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(FileTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<ISignatureVerifier, WinTrustSignatureVerifier>();
        services.AddHttpContextAccessor();
        services.AddHttpClient("files", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IRebootScheduler, Win32RebootScheduler>();
        services.AddSingleton<FileDeployer>();
        services.AddSingleton<FileTicketStore>();
        services.AddSingleton<FileLinkIssuer>();
    }

    public override void MapEndpoints(WebApplication app, VestigoOptions options)
    {
        IEndpointConventionBuilder put = app.MapPut("/files/{root}/{**path}", PutAsync);
        IEndpointConventionBuilder get = app.MapGet("/files/{root}/{**path}", Get);
        if (options.AuthenticationEnabled)
        {
            // Both endpoints accept the API token or a one-time ticket and enforce that themselves.
            put.AllowAnonymous();
            get.AllowAnonymous();
        }
    }

    private static bool IsAuthenticated(HttpContext context, VestigoOptions options) =>
        !options.AuthenticationEnabled || context.User.Identity?.IsAuthenticated == true;

    private static IResult Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(
            new { error = "invalid_ticket", message = "Provide a valid API token or an unused, unexpired ticket for this path." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static async Task<IResult> PutAsync(
        string root,
        string? path,
        HttpContext context,
        RootPolicy roots,
        FileDeployer deployer,
        VestigoOptions options,
        FileTicketStore tickets,
        bool overwrite = false,
        bool deferToReboot = false,
        string? ticket = null)
    {
        PathCheck check = ResolveRequest(roots, root, path, RootAccess.Write);
        FileTicketStore.Reservation? reservation = null;
        if (!IsAuthenticated(context, options))
        {
            if (!check.Success || (reservation = tickets.TryReserve(ticket, "PUT", check.FullPath!)) is null)
            {
                return Unauthorized(context);
            }

            NoStore(context);
            overwrite = reservation.Overwrite;
            deferToReboot = reservation.DeferToReboot;
        }

        bool succeeded = false;
        try
        {
            (IResult outcome, bool ok) = await PutCoreAsync(context, check, deployer, options, overwrite, deferToReboot);
            succeeded = ok;
            return outcome;
        }
        finally
        {
            if (reservation is not null)
            {
                if (succeeded)
                {
                    tickets.Consume(reservation);
                }
                else
                {
                    tickets.Release(reservation);
                }
            }
        }
    }

    private static async Task<(IResult Outcome, bool Success)> PutCoreAsync(
        HttpContext context,
        PathCheck check,
        FileDeployer deployer,
        VestigoOptions options,
        bool overwrite,
        bool deferToReboot)
    {
        if (!check.Success)
        {
            return (Problem(check.ErrorCode!, check.Error!), false);
        }

        long max = options.Files.MaxUploadBytes;
        if (context.Request.ContentLength > max)
        {
            return (Problem("too_large", $"Upload exceeds the {max} byte limit."), false);
        }

        IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = null;
        }

        string? expected = context.Request.Headers["X-Content-SHA256"].FirstOrDefault();
        OperationResult<DeployResult> result = await deployer.DeployAsync(
            context.Request.Body,
            new DeployRequest(check.FullPath!, overwrite, deferToReboot, max, expected),
            context.RequestAborted);
        return result.Success
            ? (Results.Json(result.Value), true)
            : (Problem(result.ErrorCode!, result.Error!), false);
    }

    private static async Task<IResult> Get(
        string root,
        string? path,
        HttpContext context,
        RootPolicy roots,
        VestigoOptions options,
        FileTicketStore tickets,
        string? ticket = null)
    {
        PathCheck check = ResolveRequest(roots, root, path, RootAccess.Read);
        FileTicketStore.Reservation? reservation = null;
        if (!IsAuthenticated(context, options))
        {
            if (!check.Success || (reservation = tickets.TryReserve(ticket, "GET", check.FullPath!)) is null)
            {
                return Unauthorized(context);
            }
        }

        if (!check.Success)
        {
            return Problem(check.ErrorCode!, check.Error!);
        }

        if (!File.Exists(check.FullPath))
        {
            if (reservation is not null)
            {
                tickets.Release(reservation);
            }

            return Problem("not_found", "File does not exist.");
        }

        if (reservation is not null)
        {
            return await StreamOnceAsync(context, check.FullPath!, tickets, reservation);
        }

        return Results.File(
            check.FullPath!,
            "application/octet-stream",
            Path.GetFileName(check.FullPath),
            enableRangeProcessing: true);
    }

    /// <summary>Streams the whole file without range support and consumes the ticket only after the last byte was written.</summary>
    private static async Task<IResult> StreamOnceAsync(
        HttpContext context,
        string fullPath,
        FileTicketStore tickets,
        FileTicketStore.Reservation reservation)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tickets.Release(reservation);
            return Problem(ex is IOException ? "file_locked" : "access_denied", "File cannot be opened.");
        }

        await using (stream)
        {
            try
            {
                NoStore(context);
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = stream.Length;
                context.Response.Headers.ContentDisposition =
                    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = Path.GetFileName(fullPath) }.ToString();
                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            catch
            {
                tickets.Release(reservation);
                throw;
            }
        }

        tickets.Consume(reservation);
        return Results.Empty;
    }

    private static PathCheck ResolveRequest(RootPolicy roots, string root, string? path, RootAccess access)
    {
        if (string.IsNullOrEmpty(path) || root.Contains(':') || path.Contains(':'))
        {
            return PathCheck.Fail("invalid_path", "Use /files/{root}/{path} with a file path and no ':' characters.");
        }

        PathCheck check = roots.Resolve($"{root}:{path}", access);
        return check.Success ? FileAccessGuard.Validate(check) : check;
    }

    private static IResult Problem(string code, string message)
    {
        int status = code switch
        {
            "unknown_root" or "not_found" => StatusCodes.Status404NotFound,
            "access_denied" => StatusCodes.Status403Forbidden,
            "already_exists" or "is_directory" => StatusCodes.Status409Conflict,
            "too_large" => StatusCodes.Status413PayloadTooLarge,
            "hash_mismatch" => StatusCodes.Status422UnprocessableEntity,
            "file_locked" => StatusCodes.Status423Locked,
            "io_error" or "reboot_schedule_failed" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { error = code, message }, statusCode: status);
    }
}
