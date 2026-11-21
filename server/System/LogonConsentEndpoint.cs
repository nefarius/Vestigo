using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

using Vestigo.Configuration;
using Vestigo.Hosting.Components;

namespace Vestigo.SystemInfo;

/// <summary>
///     One-time consent form for AutoLogon and interactive logon. The ticket is the only credential.
///     The page is MudBlazor SSR: no <c>/_blazor</c> circuit and no API token.
/// </summary>
public static class LogonConsentEndpoint
{
    public const string PathPrefix = "/system/logon/consent";

    internal const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public static void Map(WebApplication app, VestigoOptions options)
    {
        IEndpointConventionBuilder get = app.MapGet(PathPrefix + "/{id}", GetAsync);
        IEndpointConventionBuilder post = app.MapPost(PathPrefix + "/{id}", PostAsync);
        if (options.AuthenticationEnabled)
        {
            get.AllowAnonymous();
            post.AllowAnonymous();
        }
    }

    private static Task<IResult> GetAsync(
        string id,
        HttpContext context,
        LogonConsentStore store,
        IAntiforgery antiforgery,
        string? ticket = null)
    {
        if (IsPrefetch(context) || HttpMethods.IsHead(context.Request.Method))
        {
            return Task.FromResult(PrefetchOrHead());
        }

        ApplyHeaders(context);
        if (!store.TryOpen(id, ticket, out LogonConsentStore.PendingConsent? pending, out string? errorCode, out string? error))
        {
            return Task.FromResult<IResult>(Page(new LogonConsentPageModel
            {
                Title = "Consent unavailable",
                Error = error ?? "The consent request is unknown or has expired.",
                ShowForm = false
            }));
        }

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        return Task.FromResult<IResult>(Page(FormModel(pending!, ticket!, tokens, context, remaining: pending!.RemainingAttempts)));
    }

    private static async Task<IResult> PostAsync(
        string id,
        HttpContext context,
        LogonConsentStore store,
        AutoLogonService autoLogon,
        InteractiveLogonService interactive,
        IAntiforgery antiforgery)
    {
        if (IsPrefetch(context))
        {
            return PrefetchOrHead();
        }

        ApplyHeaders(context);
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Page(new LogonConsentPageModel
            {
                Title = "Consent unavailable",
                Error = "The form could not be validated. Reload the consent link and try again.",
                ShowForm = false
            });
        }

        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        string ticket = form["ticket"].ToString();
        string decision = form["decision"].ToString();
        char[] password = form["password"].ToString().ToCharArray();
        try
        {
            if (!store.TryOpen(id, ticket, out LogonConsentStore.PendingConsent? pending, out _, out string? error))
            {
                return Page(new LogonConsentPageModel
                {
                    Title = "Consent unavailable",
                    Error = error ?? "The consent request is unknown or has expired.",
                    ShowForm = false
                });
            }

            if (string.Equals(decision, "reject", StringComparison.OrdinalIgnoreCase))
            {
                store.Complete(pending!.RequestId, LogonRequestStatus.Rejected, "rejected", "The user rejected the request.");
                return Page(new LogonConsentPageModel
                {
                    Title = "Request rejected",
                    Operation = OperationName(pending.Kind),
                    Account = LogonAccount.Display(pending.UserName, pending.Domain),
                    Message = "The request was rejected. No password was stored or submitted to Windows.",
                    ShowForm = false
                });
            }

            if (!string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase))
            {
                AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                return Page(FormModel(pending!, ticket, tokens, context, remaining: pending!.RemainingAttempts, error: "Choose Approve or Reject."));
            }

            if (password.Length == 0)
            {
                AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                return Page(FormModel(pending!, ticket, tokens, context, remaining: pending!.RemainingAttempts, error: "Enter the Windows password."));
            }

            OperationResult<LogonRequestView> result = pending!.Kind == LogonConsentKind.InteractiveLogon
                ? await interactive.ApproveAsync(pending, password, context.RequestAborted).ConfigureAwait(false)
                : autoLogon.Approve(pending, password);

            if (!result.Success)
            {
                LogonRequestView? view = store.Peek(pending.RequestId);
                bool retry = result.ErrorCode is "invalid_credentials" &&
                             view?.Status == LogonConsentStore.StatusName(LogonRequestStatus.Pending);
                if (retry)
                {
                    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                    return Page(FormModel(
                        pending,
                        ticket,
                        tokens,
                        context,
                        remaining: pending.RemainingAttempts - 1,
                        error: result.Error));
                }

                return Page(new LogonConsentPageModel
                {
                    Title = "Consent failed",
                    Operation = OperationName(pending.Kind),
                    Account = LogonAccount.Display(pending.UserName, pending.Domain),
                    Error = result.Error ?? "The request failed.",
                    ShowForm = false
                });
            }

            return Page(new LogonConsentPageModel
            {
                Title = "Request completed",
                Operation = OperationName(pending.Kind),
                Account = LogonAccount.Display(pending.UserName, pending.Domain),
                Message = pending.Kind == LogonConsentKind.InteractiveLogon
                    ? "Windows received the credentials. The console should unlock or sign in shortly."
                    : "AutoLogon is enabled. The password was stored as an LSA secret.",
                ShowForm = false
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        }
    }

    private static RazorComponentResult<LogonConsentPage> Page(LogonConsentPageModel model) =>
        new(new Dictionary<string, object?> { ["Model"] = model });

    private static LogonConsentPageModel FormModel(
        LogonConsentStore.PendingConsent pending,
        string ticket,
        AntiforgeryTokenSet tokens,
        HttpContext context,
        int remaining,
        string? error = null) =>
        new()
        {
            Title = "Approve this request",
            Operation = OperationName(pending.Kind),
            Account = LogonAccount.Display(pending.UserName, pending.Domain),
            Details = Details(pending),
            Ticket = ticket,
            AntiforgeryField = tokens.FormFieldName,
            AntiforgeryValue = tokens.RequestToken,
            ShowForm = true,
            HttpWarning = HttpWarning(context),
            RemainingAttempts = remaining,
            Error = error
        };

    private static string OperationName(LogonConsentKind kind) =>
        kind == LogonConsentKind.InteractiveLogon ? "Sign in / Unlock" : "Auto-Logon";

    private static string Details(LogonConsentStore.PendingConsent pending)
    {
        List<string> parts = [$"Expires {pending.ExpiresUtc:u}."];
        if (pending.Kind == LogonConsentKind.AutoLogonEnable)
        {
            parts.Add(pending.LogonCount is { } count
                ? $"Automatic logons remaining: {count}."
                : "AutoLogon stays on until it is disabled.");
            if (pending.ForceAutoLogon)
            {
                parts.Add("ForceAutoLogon is on (Shift will not skip it).");
            }
        }

        return string.Join(" ", parts);
    }

    private static bool HttpWarning(HttpContext context) =>
        !context.Request.IsHttps &&
        context.Connection.RemoteIpAddress is { } remote &&
        !System.Net.IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    private static void ApplyHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static bool IsPrefetch(HttpContext context) =>
        context.Request.Headers["Sec-Fetch-Purpose"] == "prefetch" ||
        context.Request.Headers["Purpose"] == "prefetch";

    private static IResult PrefetchOrHead()
    {
        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }
}
