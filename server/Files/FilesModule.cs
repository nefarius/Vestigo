using Microsoft.AspNetCore.Http.Features;

using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.Security;

namespace Vestigo.Files;

/// <summary>File browsing, transfer and replacement inside configured roots.</summary>
public sealed class FilesModule : VestigoModule
{
    public override string Name => "Files";

    public override string Description => "Root-confined file tools plus authenticated PUT/GET transfer endpoints.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(FileTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient("files", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IRebootScheduler, Win32RebootScheduler>();
        services.AddSingleton<FileDeployer>();
    }

    public override void MapEndpoints(WebApplication app, VestigoOptions options)
    {
        IEndpointConventionBuilder put = app.MapPut("/files/{root}/{**path}", PutAsync);
        IEndpointConventionBuilder get = app.MapGet("/files/{root}/{**path}", Get);
        if (options.AuthenticationEnabled)
        {
            put.RequireAuthorization();
            get.RequireAuthorization();
        }
    }

    private static async Task<IResult> PutAsync(
        string root,
        string? path,
        HttpContext context,
        RootPolicy roots,
        FileDeployer deployer,
        VestigoOptions options,
        bool overwrite = false,
        bool deferToReboot = false)
    {
        PathCheck check = ResolveRequest(roots, root, path, RootAccess.Write);
        if (!check.Success)
        {
            return Problem(check.ErrorCode!, check.Error!);
        }

        long max = options.Files.MaxUploadBytes;
        if (context.Request.ContentLength > max)
        {
            return Problem("too_large", $"Upload exceeds the {max} byte limit.");
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
            ? Results.Json(result.Value)
            : Problem(result.ErrorCode!, result.Error!);
    }

    private static IResult Get(string root, string? path, RootPolicy roots)
    {
        PathCheck check = ResolveRequest(roots, root, path, RootAccess.Read);
        if (!check.Success)
        {
            return Problem(check.ErrorCode!, check.Error!);
        }

        if (!File.Exists(check.FullPath))
        {
            return Problem("not_found", "File does not exist.");
        }

        return Results.File(
            check.FullPath!,
            "application/octet-stream",
            Path.GetFileName(check.FullPath),
            enableRangeProcessing: true);
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
