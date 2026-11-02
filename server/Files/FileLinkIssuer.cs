using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Files;

/// <summary>A ready-to-use transfer URL with copy-paste commands that never contain the API token.</summary>
/// <param name="Url">Full URL. Contains a one-time ticket when authentication is enabled.</param>
/// <param name="ExpiresUtc">When the ticket expires, or null when no ticket was needed.</param>
/// <param name="SingleUse">True when the URL stops working after one successful transfer.</param>
public sealed record FileLink(string Url, DateTimeOffset? ExpiresUtc, bool SingleUse, string Curl, string PowerShell);

/// <summary>Builds <c>/files</c> URLs and mints one-time tickets for them.</summary>
public sealed class FileLinkIssuer(
    VestigoOptions options,
    RootPolicy roots,
    FileTicketStore tickets,
    IHttpContextAccessor? httpContext = null)
{
    /// <summary>Creates a download link for an existing file that lies inside a root with Read access.</summary>
    public FileLink? TryCreateDownload(string fullPath)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (RootDefinition root in roots.Roots.Where(root => root.Access.HasFlag(RootAccess.Read)))
        {
            // A volume root such as C:\ already ends with a separator that TrimEndingDirectorySeparator keeps.
            // Use the same link-following resolution as RootPolicy so the prefix matches resolved candidate paths.
            PathCheck resolvedRoot = roots.Resolve($"{root.Name}:", RootAccess.Read);
            if (!resolvedRoot.Success || string.IsNullOrEmpty(resolvedRoot.FullPath))
            {
                continue;
            }

            string rootPath = Path.TrimEndingDirectorySeparator(resolvedRoot.FullPath);
            string prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = candidate[prefix.Length..].Replace('\\', '/');
            if (relative.Length == 0 || relative.Contains(':') || relative.Split('/').Any(segment => segment is "." or ".."))
            {
                continue;
            }

            PathCheck check = roots.Resolve($"{root.Name}:{relative}", RootAccess.Read);
            if (!check.Success || FileAccessGuard.Validate(check) is { Success: false })
            {
                continue;
            }

            return Build(root.Name, relative, "GET", check.FullPath!, overwrite: false, deferToReboot: false);
        }

        return null;
    }

    /// <summary>Creates an upload link. <paramref name="fullPath" /> is the resolved destination.</summary>
    public FileLink CreateUpload(string root, string relative, string fullPath, bool overwrite, bool deferToReboot) =>
        Build(root, relative, "PUT", fullPath, overwrite, deferToReboot);

    private FileLink Build(string root, string relative, string method, string fullPath, bool overwrite, bool deferToReboot)
    {
        string encoded = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        string url = $"{BaseUrl()}/files/{Uri.EscapeDataString(root)}/{encoded}";
        List<string> query = [];
        if (method == "PUT")
        {
            query.Add($"overwrite={overwrite.ToString().ToLowerInvariant()}");
            query.Add($"deferToReboot={deferToReboot.ToString().ToLowerInvariant()}");
        }

        DateTimeOffset? expires = null;
        if (options.AuthenticationEnabled)
        {
            IssuedTicket ticket = tickets.Issue(method, fullPath, overwrite, deferToReboot);
            query.Add($"ticket={ticket.Token}");
            expires = ticket.ExpiresUtc;
        }

        if (query.Count > 0)
        {
            url += "?" + string.Join('&', query);
        }

        string curl = method == "PUT"
            ? $"curl.exe -H \"X-Content-SHA256: <sha256>\" --upload-file \"<local file>\" \"{url}\""
            : $"curl.exe -o \"<local file>\" \"{url}\"";
        string powershell = method == "PUT"
            ? $"Invoke-WebRequest -Method Put -Uri '{url}' -InFile '<local file>'"
            : $"Invoke-WebRequest -Uri '{url}' -OutFile '<local file>'";
        return new FileLink(url, expires, expires is not null, curl, powershell);
    }

    private string BaseUrl()
    {
        HttpRequest? request = httpContext?.HttpContext?.Request;
        if (request is not null && request.Host.HasValue)
        {
            return $"{request.Scheme}://{request.Host.Value}";
        }

        System.Net.IPAddress address = System.Net.IPAddress.Parse(options.ListenAddress);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{options.ListenPort}";
    }
}
