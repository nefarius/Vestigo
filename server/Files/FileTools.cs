using System.ComponentModel;
using System.Security.Cryptography;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Files;

/// <summary>A configured root with free space.</summary>
public sealed record RootInfo(string Name, string Path, string Access, long? FreeBytes, long? TotalBytes);

/// <summary>One directory entry.</summary>
public sealed record DirectoryEntry(string Name, bool IsDirectory, long? Size, DateTimeOffset LastWriteUtc);

/// <summary>Directory listing, possibly truncated at the requested entry limit.</summary>
public sealed record DirectoryListing(string FullPath, IReadOnlyList<DirectoryEntry> Entries, bool Truncated);

/// <summary>Version resource of a PE file.</summary>
public sealed record VersionDetails(
    string? FileVersion,
    string? ProductVersion,
    string? ProductName,
    string? CompanyName,
    string? FileDescription,
    string? OriginalFilename);

/// <summary>File or directory metadata.</summary>
public sealed record PathInfo(
    string FullPath,
    bool IsDirectory,
    long? Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastWriteUtc,
    string Attributes,
    string? Sha256,
    VersionDetails? Version,
    SignatureInfo? Signature);

/// <summary>Result of copy, move, delete, and create operations.</summary>
public sealed record PathOperationResult(string FullPath, string? Destination, int Files);

/// <summary>Upload instructions for one path.</summary>
public sealed record UploadUrlInfo(string Url, string UrlTemplate, string Curl, string PowerShell, long MaxUploadBytes);

/// <summary>Result of scheduling a reboot-time operation.</summary>
public sealed record RebootScheduleResult(string FullPath, bool RebootRequired);

/// <summary>MCP tools for browsing and managing files inside configured roots.</summary>
[McpServerToolType]
public sealed class FileTools(
    VestigoOptions options,
    RootPolicy roots,
    FileDeployer deployer,
    IRebootScheduler scheduler,
    JobManager jobs,
    IHttpClientFactory httpClients,
    IHttpContextAccessor? httpContext = null)
{
    private static readonly string[] PeExtensions = [".exe", ".dll", ".sys"];

    [McpServerTool(Name = "list_roots")]
    [Description("Lists the configured named roots with their access flags and free disk space.")]
    public IReadOnlyList<RootInfo> ListRoots() =>
        roots.Roots.Select(root =>
        {
            long? free = null;
            long? total = null;
            try
            {
                string? driveRoot = System.IO.Path.GetPathRoot(root.Path);
                if (!string.IsNullOrEmpty(driveRoot) && !driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    DriveInfo drive = new(driveRoot);
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Space is informational only.
            }

            return new RootInfo(root.Name, root.Path, root.Access.ToString(), free, total);
        }).ToList();

    [McpServerTool(Name = "list_directory")]
    [Description("Lists the entries of a directory inside a root with Read access.")]
    public OperationResult<DirectoryListing> ListDirectory(
        [Description("Directory as root:relative\\path or an absolute path inside a root.")] string path,
        [Description("Maximum entries to return, 1 to 5000.")] int maxEntries = 500)
    {
        PathCheck check = Resolve(path, RootAccess.Read, mustExist: true);
        if (!check.Success)
        {
            return Fail<DirectoryListing>(check);
        }

        if (!Directory.Exists(check.FullPath))
        {
            return OperationResult<DirectoryListing>.Fail("not_directory", $"Not a directory: '{check.FullPath}'.");
        }

        return Guard(() =>
        {
            int limit = Math.Clamp(maxEntries, 1, 5000);
            List<DirectoryEntry> entries = [];
            bool truncated = false;
            foreach (FileSystemInfo info in new DirectoryInfo(check.FullPath!).EnumerateFileSystemInfos("*", Enumeration))
            {
                if (entries.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                bool directory = info is DirectoryInfo;
                entries.Add(new DirectoryEntry(
                    info.Name,
                    directory,
                    directory ? null : ((FileInfo)info).Length,
                    info.LastWriteTimeUtc));
            }

            return OperationResult<DirectoryListing>.Ok(new DirectoryListing(check.FullPath!, entries, truncated));
        });
    }

    [McpServerTool(Name = "stat_path")]
    [Description("Returns metadata for a file or directory: size, times, optional SHA-256, PE version info and Authenticode status for exe/dll/sys files.")]
    public OperationResult<PathInfo> StatPath(
        [Description("Path as root:relative\\path or an absolute path inside a root with Read access.")] string path,
        [Description("Compute the SHA-256 of a file.")] bool computeSha256 = false,
        CancellationToken cancellationToken = default)
    {
        PathCheck check = Resolve(path, RootAccess.Read, mustExist: true);
        if (!check.Success)
        {
            return Fail<PathInfo>(check);
        }

        return Guard(() =>
        {
            string full = check.FullPath!;
            if (Directory.Exists(full))
            {
                DirectoryInfo directory = new(full);
                return OperationResult<PathInfo>.Ok(new PathInfo(
                    full, true, null, directory.CreationTimeUtc, directory.LastWriteTimeUtc,
                    directory.Attributes.ToString(), null, null, null));
            }

            FileInfo file = new(full);
            string? hash = null;
            if (computeSha256)
            {
                using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }

            VersionDetails? version = null;
            SignatureInfo? signature = null;
            if (PeExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(full);
                version = new VersionDetails(
                    info.FileVersion, info.ProductVersion, info.ProductName, info.CompanyName,
                    info.FileDescription, info.OriginalFilename);
                signature = AuthenticodeVerifier.Verify(full);
            }

            return OperationResult<PathInfo>.Ok(new PathInfo(
                full, false, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc,
                file.Attributes.ToString(), hash, version, signature));
        });
    }

    [McpServerTool(Name = "copy_path")]
    [Description("Copies a file or directory tree. Source needs Read access, destination needs Write access. Links inside directory trees are skipped.")]
    public OperationResult<PathOperationResult> CopyPath(
        [Description("Source path.")] string source,
        [Description("Destination path (the new name, not the parent directory).")] string destination,
        [Description("Replace existing destination files.")] bool overwrite = false)
    {
        PathCheck from = Resolve(source, RootAccess.Read, mustExist: true);
        if (!from.Success)
        {
            return Fail<PathOperationResult>(from);
        }

        PathCheck to = Resolve(destination, RootAccess.Write);
        if (!to.Success)
        {
            return Fail<PathOperationResult>(to);
        }

        return Guard(() =>
        {
            if (Directory.Exists(from.FullPath))
            {
                if (IsSameOrChild(to.FullPath!, from.FullPath!))
                {
                    return OperationResult<PathOperationResult>.Fail("invalid_destination", "Destination lies inside the source directory.");
                }

                if (File.Exists(to.FullPath))
                {
                    return OperationResult<PathOperationResult>.Fail("already_exists", "Destination is an existing file.");
                }

                int count = CopyDirectory(from.FullPath!, to.FullPath!, overwrite);
                return OperationResult<PathOperationResult>.Ok(new PathOperationResult(from.FullPath!, to.FullPath, count));
            }

            if (Directory.Exists(to.FullPath))
            {
                return OperationResult<PathOperationResult>.Fail("is_directory", "Destination is an existing directory.");
            }

            if (File.Exists(to.FullPath) && !overwrite)
            {
                return OperationResult<PathOperationResult>.Fail("already_exists", $"Destination exists: '{to.FullPath}'.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to.FullPath)!);
            File.Copy(from.FullPath!, to.FullPath!, overwrite);
            return OperationResult<PathOperationResult>.Ok(new PathOperationResult(from.FullPath!, to.FullPath, 1));
        });
    }

    [McpServerTool(Name = "move_path")]
    [Description("Moves or renames a file or directory. Both paths need Write access. Refuses to move a root itself.")]
    public OperationResult<PathOperationResult> MovePath(
        [Description("Source path.")] string source,
        [Description("Destination path (the new name).")] string destination,
        [Description("Replace an existing destination file.")] bool overwrite = false)
    {
        PathCheck from = Resolve(source, RootAccess.Write, mustExist: true);
        if (!from.Success)
        {
            return Fail<PathOperationResult>(from);
        }

        PathCheck to = Resolve(destination, RootAccess.Write);
        if (!to.Success)
        {
            return Fail<PathOperationResult>(to);
        }

        if (IsRoot(from.FullPath!))
        {
            return OperationResult<PathOperationResult>.Fail("is_root", "A root itself cannot be moved.");
        }

        return Guard(() =>
        {
            if (Directory.Exists(from.FullPath))
            {
                if (IsSameOrChild(to.FullPath!, from.FullPath!))
                {
                    return OperationResult<PathOperationResult>.Fail("invalid_destination", "Destination lies inside the source directory.");
                }

                if (Directory.Exists(to.FullPath) || File.Exists(to.FullPath))
                {
                    return OperationResult<PathOperationResult>.Fail("already_exists", $"Destination exists: '{to.FullPath}'.");
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to.FullPath)!);
                Directory.Move(from.FullPath!, to.FullPath!);
            }
            else
            {
                if (Directory.Exists(to.FullPath))
                {
                    return OperationResult<PathOperationResult>.Fail("is_directory", "Destination is an existing directory.");
                }

                if (File.Exists(to.FullPath) && !overwrite)
                {
                    return OperationResult<PathOperationResult>.Fail("already_exists", $"Destination exists: '{to.FullPath}'.");
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to.FullPath)!);
                File.Move(from.FullPath!, to.FullPath!, overwrite);
            }

            return OperationResult<PathOperationResult>.Ok(new PathOperationResult(from.FullPath!, to.FullPath, 1));
        });
    }

    [McpServerTool(Name = "delete_path")]
    [Description("Deletes a file or directory inside a root with Write access. Refuses to delete a root itself. Non-empty directories need recursive=true.")]
    public OperationResult<PathOperationResult> DeletePath(
        [Description("Path to delete.")] string path,
        [Description("Delete directory contents too.")] bool recursive = false)
    {
        PathCheck check = Resolve(path, RootAccess.Write, mustExist: true);
        if (!check.Success)
        {
            return Fail<PathOperationResult>(check);
        }

        if (IsRoot(check.FullPath!))
        {
            return OperationResult<PathOperationResult>.Fail("is_root", "A root itself cannot be deleted.");
        }

        return Guard(() =>
        {
            if (Directory.Exists(check.FullPath))
            {
                if (!recursive && Directory.EnumerateFileSystemEntries(check.FullPath).Any())
                {
                    return OperationResult<PathOperationResult>.Fail("not_empty", "Directory is not empty. Pass recursive=true.");
                }

                Directory.Delete(check.FullPath, recursive);
            }
            else
            {
                File.Delete(check.FullPath!);
            }

            return OperationResult<PathOperationResult>.Ok(new PathOperationResult(check.FullPath!, null, 1));
        });
    }

    [McpServerTool(Name = "make_directory")]
    [Description("Creates a directory, including missing parents, inside a root with Write access.")]
    public OperationResult<PathOperationResult> MakeDirectory(
        [Description("Directory path.")] string path)
    {
        PathCheck check = Resolve(path, RootAccess.Write);
        if (!check.Success)
        {
            return Fail<PathOperationResult>(check);
        }

        return Guard(() =>
        {
            if (File.Exists(check.FullPath))
            {
                return OperationResult<PathOperationResult>.Fail("already_exists", "A file with that name exists.");
            }

            Directory.CreateDirectory(check.FullPath!);
            return OperationResult<PathOperationResult>.Ok(new PathOperationResult(check.FullPath!, null, 0));
        });
    }

    [McpServerTool(Name = "fetch_file")]
    [Description("Downloads an HTTP(S) URL or copies a UNC file into a root with Write access as a background job. Poll with wait_job and read_job_output; the output ends with the stored file's path, size and SHA-256.")]
    public OperationResult<JobInfo> FetchFile(
        [Description("http:// or https:// URL, or a UNC path such as \\\\server\\share\\file.sys.")] string source,
        [Description("Destination file as root:relative\\path.")] string destination,
        [Description("Expected SHA-256 (hex). The file is discarded on mismatch.")] string? expectedSha256 = null,
        [Description("Replace an existing destination file.")] bool overwrite = false)
    {
        PathCheck to = Resolve(destination, RootAccess.Write);
        if (!to.Success)
        {
            return Fail<JobInfo>(to);
        }

        bool unc = source.StartsWith(@"\\", StringComparison.Ordinal);
        Uri? uri = null;
        if (!unc && (!Uri.TryCreate(source, UriKind.Absolute, out uri) ||
                     (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return OperationResult<JobInfo>.Fail("invalid_source", "Source must be an http(s) URL or a UNC path.");
        }

        string target = to.FullPath!;
        long limit = options.Files.MaxUploadBytes;
        string label = unc ? source : uri!.GetLeftPart(UriPartial.Path);
        return jobs.Start("fetch_file", $"Fetch {label} to {target}", async (context, cancellationToken) =>
        {
            Stream stream;
            HttpResponseMessage? response = null;
            if (unc)
            {
                stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            }
            else
            {
                HttpClient client = httpClients.CreateClient("files");
                response = await client.GetAsync(uri!, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    context.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    response.Dispose();
                    return 1;
                }

                stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }

            using (response)
            await using (stream)
            {
                OperationResult<DeployResult> result = await deployer.DeployAsync(
                    stream,
                    new DeployRequest(target, overwrite, false, limit, expectedSha256),
                    cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    context.WriteLine($"{result.ErrorCode}: {result.Error}");
                    return 1;
                }

                DeployResult value = result.Value!;
                context.WriteLine($"path={value.FullPath} size={value.Size} sha256={value.Sha256} replacedExisting={value.ReplacedExisting}");
                return 0;
            }
        });
    }

    [McpServerTool(Name = "get_upload_url")]
    [Description("Returns the HTTP PUT URL for uploading a file into a root, with curl and Invoke-WebRequest examples. Uploads need Write access to the root.")]
    public OperationResult<UploadUrlInfo> GetUploadUrl(
        [Description("Destination as root:relative\\path.")] string path,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description("Replace a locked file at the next reboot instead of renaming it aside.")] bool deferToReboot = false)
    {
        PathCheck check = Resolve(path, RootAccess.Write);
        if (!check.Success)
        {
            return Fail<UploadUrlInfo>(check);
        }

        int colon = path.IndexOf(':');
        if (colon < 2 || path[..colon].Contains('\\') || path[..colon].Contains('/'))
        {
            return OperationResult<UploadUrlInfo>.Fail("invalid_path", "Use the root:relative\\path form so the URL can address the root by name.");
        }

        string root = path[..colon];
        string relative = path[(colon + 1)..].Replace('\\', '/').Trim('/');
        if (relative.Length == 0 || relative.Contains(':') || relative.Split('/').Any(segment => segment is "." or ".."))
        {
            return OperationResult<UploadUrlInfo>.Fail("invalid_path", "Path needs a relative file name without ':' or '..'.");
        }

        string baseUrl = BaseUrl();
        string encoded = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        string query = $"?overwrite={overwrite.ToString().ToLowerInvariant()}&deferToReboot={deferToReboot.ToString().ToLowerInvariant()}";
        string url = $"{baseUrl}/files/{Uri.EscapeDataString(root)}/{encoded}{query}";
        string template = $"{baseUrl}/files/{{root}}/{{path}}?overwrite=false&deferToReboot=false";
        string auth = options.AuthenticationEnabled ? " -H \"Authorization: Bearer <token>\"" : string.Empty;
        string psAuth = options.AuthenticationEnabled ? " -Headers @{ Authorization = 'Bearer <token>' }" : string.Empty;
        string curl = $"curl.exe -X PUT{auth} -H \"X-Content-SHA256: <sha256>\" --data-binary \"@<local file>\" \"{url}\"";
        string powershell = $"Invoke-WebRequest -Method Put -Uri '{url}' -InFile '<local file>'{psAuth}";
        return OperationResult<UploadUrlInfo>.Ok(new UploadUrlInfo(url, template, curl, powershell, options.Files.MaxUploadBytes));
    }

    [McpServerTool(Name = "delete_file_on_reboot")]
    [Description("Schedules a file for deletion at the next reboot (MoveFileEx delay-until-reboot). Needs Write access and administrator rights.")]
    public OperationResult<RebootScheduleResult> DeleteFileOnReboot(
        [Description("File path inside a root with Write access.")] string path)
    {
        PathCheck check = Resolve(path, RootAccess.Write, mustExist: true);
        if (!check.Success)
        {
            return Fail<RebootScheduleResult>(check);
        }

        if (IsRoot(check.FullPath!))
        {
            return OperationResult<RebootScheduleResult>.Fail("is_root", "A root itself cannot be deleted.");
        }

        int error = scheduler.ScheduleDelete(check.FullPath!);
        return error == 0
            ? OperationResult<RebootScheduleResult>.Ok(new RebootScheduleResult(check.FullPath!, true))
            : OperationResult<RebootScheduleResult>.Fail("reboot_schedule_failed", FileDeployer.ErrorText(error));
    }

    private static readonly EnumerationOptions Enumeration = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

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

    private PathCheck Resolve(string path, RootAccess access, bool mustExist = false)
    {
        PathCheck check = mustExist ? roots.ResolveExisting(path, access) : roots.Resolve(path, access);
        return check.Success ? FileAccessGuard.Validate(check) : check;
    }

    private bool IsRoot(string fullPath) => FileAccessGuard.IsRoot(roots, fullPath);

    private static bool IsSameOrChild(string candidate, string parent)
    {
        string prefix = parent.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return string.Equals(candidate.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        int count = 0;
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", Enumeration))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            string next = System.IO.Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
            {
                count += CopyDirectory(entry.FullName, next, overwrite);
            }
            else
            {
                File.Copy(entry.FullName, next, overwrite);
                count++;
            }
        }

        return count;
    }

    private static OperationResult<T> Fail<T>(PathCheck check) => OperationResult<T>.Fail(check.ErrorCode!, check.Error!);

    private static OperationResult<T> Guard<T>(Func<OperationResult<T>> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return OperationResult<T>.Fail("io_error", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Extra path checks shared by the tools and HTTP endpoints.</summary>
internal static class FileAccessGuard
{
    /// <summary>Rejects NTFS alternate data stream syntax in a resolved path.</summary>
    public static PathCheck Validate(PathCheck check)
    {
        string full = check.FullPath!;
        int start = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? 6 : 2;
        return full.Length > start && full.IndexOf(':', start) >= 0
            ? PathCheck.Fail("invalid_path", "Alternate data stream syntax is not allowed.")
            : check;
    }

    public static bool IsRoot(RootPolicy roots, string fullPath)
    {
        string normalized = System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        return roots.Roots.Any(root => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root.Path)),
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }
}
