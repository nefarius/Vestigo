using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

internal sealed class FakeRebootScheduler : IRebootScheduler
{
    public List<string> Deletes { get; } = [];

    public List<(string Source, string Destination)> Replaces { get; } = [];

    public int Result { get; set; }

    public int ScheduleDelete(string path)
    {
        Deletes.Add(path);
        return Result;
    }

    public int ScheduleReplace(string source, string destination)
    {
        Replaces.Add((source, destination));
        return Result;
    }
}

public class FileDeployerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-files-").FullName;
    private readonly FakeRebootScheduler _scheduler = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test may still hold a mapped view during teardown.
        }
    }

    private FileDeployer Deployer() => new(_scheduler);

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string Target(string name = "driver.sys") => Path.Combine(_root, "sub", name);

    [Fact]
    public async Task Deploy_CreatesNewFileWithHashAndNoLeftovers()
    {
        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("hello"), new DeployRequest(Target(), ExpectedSha256: Sha("hello").ToLowerInvariant()), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Sha("hello"), result.Value!.Sha256);
        Assert.Equal(5, result.Value.Size);
        Assert.False(result.Value.ReplacedExisting);
        Assert.Equal("hello", File.ReadAllText(Target()));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }

    [Fact]
    public async Task Deploy_RefusesExistingWithoutOverwrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");

        OperationResult<DeployResult> result = await Deployer().DeployAsync(Bytes("new"), new DeployRequest(Target()), default);

        Assert.Equal("already_exists", result.ErrorCode);
        Assert.Equal("old", File.ReadAllText(Target()));
    }

    [Fact]
    public async Task Deploy_ReplacesExistingAtomically()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");

        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("new"), new DeployRequest(Target(), Overwrite: true), default);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.ReplacedExisting);
        Assert.False(result.Value.RebootRequired);
        Assert.Equal("new", File.ReadAllText(Target()));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }

    [Fact]
    public async Task Deploy_DiscardsContentOnHashMismatch()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");

        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("new"), new DeployRequest(Target(), Overwrite: true, ExpectedSha256: Sha("other")), default);

        Assert.Equal("hash_mismatch", result.ErrorCode);
        Assert.Equal("old", File.ReadAllText(Target()));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }

    [Fact]
    public async Task Deploy_RejectsMalformedHash()
    {
        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("x"), new DeployRequest(Target(), ExpectedSha256: "abc"), default);

        Assert.Equal("invalid_hash", result.ErrorCode);
    }

    [Fact]
    public async Task Deploy_EnforcesSizeLimitAndCleansTemp()
    {
        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("0123456789"), new DeployRequest(Target(), MaxBytes: 4), default);

        Assert.Equal("too_large", result.ErrorCode);
        Assert.False(File.Exists(Target()));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }

    [Fact]
    public async Task Deploy_RenamesLockedImageAsideAndSchedulesDeletion()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        // A loaded DLL behaves like a running image: it can be renamed but not deleted or overwritten. Windows ReplaceFile
        // often still succeeds on such files, so force it to fail to exercise the fallback.
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Target());
        IntPtr image = System.Runtime.InteropServices.NativeLibrary.Load(Target());
        FileDeployer deployer = new(_scheduler)
        {
            ReplaceFile = (_, _) => throw new IOException("The process cannot access the file.")
        };
        OperationResult<DeployResult> result = await deployer.DeployAsync(
            Bytes("new image"), new DeployRequest(Target(), Overwrite: true), default);

        System.Runtime.InteropServices.NativeLibrary.Free(image);
        Assert.True(result.Success, result.Error);
        DeployResult value = result.Value!;
        Assert.Equal("new image", File.ReadAllText(Target()));
        Assert.NotNull(value.AsideFile);
        Assert.Contains(".vestigo-old-", value.AsideFile, StringComparison.Ordinal);
        Assert.Equal("scheduled_on_reboot", value.AsideCleanup);
        Assert.Equal([value.AsideFile], _scheduler.Deletes);
        Assert.True(File.Exists(value.AsideFile));
        Assert.False(value.RebootRequired);
    }
    [Fact]
    public async Task Deploy_ReportsLockedWhenFileCannotBeRenamed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");
        using FileStream hold = new(Target(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("new"), new DeployRequest(Target(), Overwrite: true), default);

        Assert.Equal("file_locked", result.ErrorCode);
        hold.Dispose();
        Assert.Equal("old", File.ReadAllText(Target()));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }

    [Fact]
    public async Task Deploy_DeferToRebootSchedulesPendingReplace()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");
        using FileStream hold = new(Target(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("new"), new DeployRequest(Target(), Overwrite: true, DeferToReboot: true), default);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.RebootRequired);
        Assert.True(result.Value.ReplacedExisting);
        (string source, string destination) = Assert.Single(_scheduler.Replaces);
        Assert.Equal(Target(), destination);
        Assert.Equal("new", File.ReadAllText(source));
        Assert.StartsWith(".vestigo-upload-", Path.GetFileName(source), StringComparison.Ordinal);
        hold.Dispose();
        Assert.Equal("old", File.ReadAllText(Target()));
    }

    [Fact]
    public async Task Deploy_DeferToRebootFailureKeepsOldFileAndRemovesTemp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target())!);
        File.WriteAllText(Target(), "old");
        _scheduler.Result = 5;

        OperationResult<DeployResult> result = await Deployer().DeployAsync(
            Bytes("new"), new DeployRequest(Target(), Overwrite: true, DeferToReboot: true), default);

        Assert.Equal("reboot_schedule_failed", result.ErrorCode);
        Assert.Equal("old", File.ReadAllText(Target()));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(Target())!));
    }
}

public class FileTicketStoreTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Full(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));

    [Fact]
    public void Ticket_IsSingleUseAndBoundToMethodAndPath()
    {
        FileTicketStore store = new(new VestigoOptions(), new FakeTime());
        string token = store.Issue("GET", Full("a.png")).Token;

        Assert.Null(store.TryReserve(token, "PUT", Full("a.png")));
        Assert.Null(store.TryReserve(token, "GET", Full("b.png")));
        Assert.Null(store.TryReserve("other", "GET", Full("a.png")));
        FileTicketStore.Reservation? reservation = store.TryReserve(token, "GET", Full("a.png"));
        Assert.NotNull(reservation);
        Assert.Null(store.TryReserve(token, "GET", Full("a.png")));

        store.Consume(reservation);
        Assert.Null(store.TryReserve(token, "GET", Full("a.png")));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Store_NeverEvictsInUseTicketsAndRefusesWhenAllBusy()
    {
        FileTicketStore store = new(new VestigoOptions(), new FakeTime());
        List<string> tokens = [];
        for (int i = 0; i < FileTicketStore.MaxOutstanding; i++)
        {
            tokens.Add(store.Issue("GET", Full("f" + i)).Token);
        }

        // The oldest ticket is mid-transfer, so the next issue evicts the second oldest instead.
        Assert.NotNull(store.TryReserve(tokens[0], "GET", Full("f0")));
        string extra = store.Issue("GET", Full("extra")).Token;
        Assert.Null(store.TryReserve(tokens[1], "GET", Full("f1")));
        Assert.Null(store.TryReserve(tokens[0], "GET", Full("f0")));
        for (int i = 2; i < tokens.Count; i++)
        {
            Assert.NotNull(store.TryReserve(tokens[i], "GET", Full("f" + i)));
        }

        Assert.NotNull(store.TryReserve(extra, "GET", Full("extra")));

        Assert.Throws<InvalidOperationException>(() => store.Issue("GET", Full("overflow")));
    }

    [Fact]
    public void Ticket_PathMatchIsCaseSensitive()
    {
        FileTicketStore store = new(new VestigoOptions(), new FakeTime());
        string token = store.Issue("GET", Full("a.png")).Token;

        // Case-sensitive directories can hold case-distinct files, so a ticket never matches another casing.
        Assert.Null(store.TryReserve(token, "GET", Full("A.png")));
        Assert.NotNull(store.TryReserve(token, "GET", Full("a.png")));
    }

    [Fact]
    public void Release_MakesTicketUsableAgain()
    {
        FileTicketStore store = new(new VestigoOptions(), new FakeTime());
        string token = store.Issue("GET", Full("a.png")).Token;

        FileTicketStore.Reservation reservation = store.TryReserve(token, "GET", Full("a.png"))!;
        store.Release(reservation);

        Assert.NotNull(store.TryReserve(token, "GET", Full("a.png")));
    }

    [Fact]
    public void Ticket_ExpiresAndIsPruned()
    {
        FakeTime time = new();
        FileTicketStore store = new(new VestigoOptions(), time);
        string token = store.Issue("GET", Full("a.png")).Token;

        time.Now += TimeSpan.FromMinutes(16);

        Assert.Null(store.TryReserve(token, "GET", Full("a.png")));
        store.Issue("GET", Full("b.png"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Store_EvictsOldestWhenFull()
    {
        FileTicketStore store = new(new VestigoOptions(), new FakeTime());
        string first = store.Issue("GET", Full("first")).Token;
        for (int i = 0; i < FileTicketStore.MaxOutstanding; i++)
        {
            store.Issue("GET", Full("f" + i));
        }

        Assert.Equal(FileTicketStore.MaxOutstanding, store.Count);
        Assert.Null(store.TryReserve(first, "GET", Full("first")));
    }
}

public class FileToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-filetools-").FullName;
    private readonly string _jobs = Directory.CreateTempSubdirectory("vestigo-filejobs-").FullName;
    private readonly FakeRebootScheduler _scheduler = new();
    private readonly VestigoOptions _options;
    private readonly JobManager _jobManager;

    public FileToolTests()
    {
        _options = new VestigoOptions
        {
            Roots =
            [
                new RootDefinition { Name = "work", Path = _root, Access = RootAccess.Read | RootAccess.Write },
                new RootDefinition { Name = "ro", Path = Directory.CreateDirectory(Path.Combine(_root, "readonly")).FullName, Access = RootAccess.Read }
            ]
        };
        _jobManager = new JobManager(_options, _jobs);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_jobs, recursive: true);
    }

    private FileTools Tools(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
    {
        RootPolicy policy = new(_options);
        return new(
            _options,
            policy,
            new FileDeployer(_scheduler),
            _scheduler,
            _jobManager,
            new StubClientFactory(handler ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new FileLinkIssuer(_options, policy, new FileTicketStore(_options)));
    }

    [Fact]
    public void ListRoots_ReportsAccessAndSpace()
    {
        IReadOnlyList<RootInfo> roots = Tools().ListRoots();

        RootInfo work = Assert.Single(roots, root => root.Name == "work");
        Assert.Equal("Read, Write", work.Access);
        Assert.True(work.FreeBytes > 0);
        Assert.Equal("Read", roots.Single(root => root.Name == "ro").Access);
    }

    [Fact]
    public void ListDirectory_ListsAndTruncates()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        OperationResult<DirectoryListing> all = Tools().ListDirectory("work:");
        OperationResult<DirectoryListing> one = Tools().ListDirectory("work:", 1);

        Assert.True(all.Success, all.Error);
        Assert.Contains(all.Value!.Entries, entry => entry.Name == "a.txt" && entry.Size == 5);
        Assert.Contains(all.Value.Entries, entry => entry.Name == "dir" && entry.IsDirectory);
        Assert.True(one.Value!.Truncated);
        Assert.Equal("unknown_root", Tools().ListDirectory("nope:").ErrorCode);
    }

    [Fact]
    public void StatPath_ReturnsHashAndPeInfoForExecutables()
    {
        string exe = Path.Combine(_root, "dotnet-copy.dll");
        File.Copy(typeof(object).Assembly.Location, exe);
        File.WriteAllText(Path.Combine(_root, "t.txt"), "abc");

        OperationResult<PathInfo> text = Tools().StatPath("work:t.txt", computeSha256: true);
        OperationResult<PathInfo> pe = Tools().StatPath("work:dotnet-copy.dll");

        Assert.Equal(Convert.ToHexString(SHA256.HashData("abc"u8)), text.Value!.Sha256);
        Assert.Null(text.Value.Signature);
        Assert.NotNull(pe.Value!.Version);
        Assert.NotNull(pe.Value.Signature);
        Assert.Contains(pe.Value.Signature.Status, new[] { "Signed", "Unsigned", "Invalid" });
    }

    [Fact]
    public void StatPath_ReportsUnsignedForPlainPe()
    {
        string exe = Path.Combine(_root, "unsigned.dll");
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(exe, bytes);

        SignatureInfo signature = AuthenticodeVerifier.Verify(exe);

        Assert.NotEqual("Signed", signature.Status);
    }

    [Fact]
    public void CopyMoveDelete_WorkInsideRoots()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        File.WriteAllText(Path.Combine(_root, "src", "nested", "f.txt"), "x");
        FileTools tools = Tools();

        OperationResult<PathOperationResult> copy = tools.CopyPath("work:src", "work:copy");
        OperationResult<PathOperationResult> again = tools.CopyPath("work:src\\nested\\f.txt", "work:copy\\nested\\f.txt");
        OperationResult<PathOperationResult> move = tools.MovePath("work:copy", "work:moved");
        OperationResult<PathOperationResult> notEmpty = tools.DeletePath("work:moved");
        OperationResult<PathOperationResult> delete = tools.DeletePath("work:moved", recursive: true);
        OperationResult<PathOperationResult> mkdir = tools.MakeDirectory("work:a\\b\\c");

        Assert.Equal(1, copy.Value!.Files);
        Assert.Equal("already_exists", again.ErrorCode);
        Assert.True(move.Success, move.Error);
        Assert.Equal("not_empty", notEmpty.ErrorCode);
        Assert.True(delete.Success, delete.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "moved")));
        Assert.True(mkdir.Success);
        Assert.True(Directory.Exists(Path.Combine(_root, "a", "b", "c")));
    }

    [Fact]
    public void Copy_RejectsCopyingIntoItself()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        Assert.Equal("invalid_destination", Tools().CopyPath("work:src", "work:src\\inner").ErrorCode);
    }

    [Fact]
    public void Delete_RefusesRootsAndEnforcesAccess()
    {
        File.WriteAllText(Path.Combine(_root, "readonly", "keep.txt"), "x");
        FileTools tools = Tools();

        Assert.Equal("is_root", tools.DeletePath("work:", recursive: true).ErrorCode);
        Assert.Equal("is_root", tools.DeletePath(_root, recursive: true).ErrorCode);
        Assert.Equal("is_root", tools.DeleteFileOnReboot("work:").ErrorCode);
        Assert.Equal("access_denied", tools.DeletePath("ro:keep.txt").ErrorCode);
        Assert.Equal("access_denied", tools.MakeDirectory("ro:new").ErrorCode);
        Assert.Equal("outside_root", tools.DeletePath("work:..\\x").ErrorCode);
        Assert.True(File.Exists(Path.Combine(_root, "readonly", "keep.txt")));
    }

    [Fact]
    public void IsRoot_TreatsAncestorsOfRootsAsRoots()
    {
        var policy = new RootPolicy(_options);

        Assert.True(FileAccessGuard.IsRoot(policy, _root));
        Assert.True(FileAccessGuard.IsRoot(policy, Path.GetDirectoryName(_root)!));
        Assert.False(FileAccessGuard.IsRoot(policy, _root + "-sibling"));
        Assert.False(FileAccessGuard.IsRoot(policy, Path.Combine(_root, "sub")));
    }

    [Fact]
    public void IsRoot_TreatsDriveVolumeRootAsRoot()
    {
        VestigoOptions options = new()
        {
            VolumeRoots = VolumeRootMode.SystemDrive,
            VolumeRootAccess = RootAccess.Read
        };
        FakeVolumeEnumerator enumerator = new() { SystemDrive = @"C:\" };
        RootPolicy policy = new(options, new VolumeRootSource(options, enumerator));

        Assert.True(FileAccessGuard.IsRoot(policy, @"C:\"));
        Assert.False(FileAccessGuard.IsRoot(policy, @"C:\Windows"));
        Assert.True(policy.Resolve(@"C:\Windows\explorer.exe", RootAccess.Read).Success);
        Assert.Equal("outside_root", policy.Resolve(@"C:\Windows\explorer.exe", RootAccess.Write).ErrorCode);
    }

    [Fact]
    public void Tools_RejectAlternateDataStreams()
    {
        Assert.Equal("invalid_path", Tools().MakeDirectory("work:a.txt:stream").ErrorCode);
    }

    [Fact]
    public void DeleteFileOnReboot_UsesSchedulerWithWriteAccess()
    {
        string file = Path.Combine(_root, "old.sys");
        File.WriteAllText(file, "x");
        FileTools tools = Tools();

        OperationResult<RebootScheduleResult> ok = tools.DeleteFileOnReboot("work:old.sys");
        OperationResult<RebootScheduleResult> denied = tools.DeleteFileOnReboot("ro:keep.txt");

        Assert.True(ok.Success);
        Assert.True(ok.Value!.RebootRequired);
        Assert.Equal([file], _scheduler.Deletes);
        Assert.False(denied.Success);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void GetUploadUrl_BuildsPutUrlAndExamples()
    {
        _options.ApiTokens.Add("t");

        OperationResult<UploadUrlInfo> result = Tools().GetUploadUrl("work:bin\\my driver.sys", overwrite: true);

        Assert.True(result.Success, result.Error);
        FileLink link = result.Value!.Link;
        Assert.StartsWith(
            "http://127.0.0.1:5098/files/work/bin/my%20driver.sys?overwrite=true&deferToReboot=false&ticket=",
            link.Url,
            StringComparison.Ordinal);
        Assert.True(link.SingleUse);
        Assert.NotNull(link.ExpiresUtc);
        Assert.Contains("--upload-file", link.Curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", link.Curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", link.PowerShell, StringComparison.Ordinal);
        Assert.Contains("Invoke-WebRequest -Method Put", link.PowerShell, StringComparison.Ordinal);
        Assert.Equal("access_denied", Tools().GetUploadUrl("ro:x.sys").ErrorCode);
        Assert.False(Tools().GetUploadUrl("work:a\\..\\..\\b").Success);
    }

    [Fact]
    public void GetUploadUrl_IsPlainWithoutApiTokens()
    {
        FileLink link = Tools().GetUploadUrl("work:a.bin").Value!.Link;

        Assert.Equal("http://127.0.0.1:5098/files/work/a.bin?overwrite=false&deferToReboot=false", link.Url);
        Assert.False(link.SingleUse);
        Assert.Null(link.ExpiresUtc);
    }

    [Fact]
    public void TryCreateDownload_WorksForVolumeRoot()
    {
        string file = Path.Combine(_root, "vol.png");
        File.WriteAllText(file, "x");
        string volume = Path.GetPathRoot(file)!;
        VestigoOptions options = new()
        {
            ApiTokens = ["t"],
            Roots = [new RootDefinition { Name = "drive-test", Path = volume, Access = RootAccess.Read }]
        };
        RootPolicy policy = new(options);

        FileLink? link = new FileLinkIssuer(options, policy, new FileTicketStore(options)).TryCreateDownload(file);

        Assert.NotNull(link);
        Assert.Contains("/files/drive-test/", link.Url, StringComparison.Ordinal);
        Assert.EndsWith("/vol.png", link.Url[..link.Url.IndexOf('?')], StringComparison.Ordinal);
    }

    [Fact]
    public void GetDownloadUrl_ReturnsTicketForExistingReadableFile()
    {
        _options.ApiTokens.Add("t");
        File.WriteAllText(Path.Combine(_root, "shot.png"), "12345");

        OperationResult<DownloadUrlInfo> result = Tools().GetDownloadUrl("work:shot.png");
        OperationResult<DownloadUrlInfo> absolute = Tools().GetDownloadUrl(Path.Combine(_root, "shot.png"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, result.Value!.Size);
        Assert.StartsWith("http://127.0.0.1:5098/files/work/shot.png?ticket=", result.Value.Link.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", result.Value.Link.Curl, StringComparison.Ordinal);
        Assert.True(absolute.Success, absolute.Error);
        Assert.NotEqual(result.Value.Link.Url, absolute.Value!.Link.Url);
        Assert.Equal("not_found", Tools().GetDownloadUrl("work:missing.png").ErrorCode);
    }

    [Fact]
    public async Task FetchFile_DownloadsHttpIntoRootAndVerifiesHash()
    {
        byte[] payload = "payload"u8.ToArray();
        FileTools tools = Tools(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        OperationResult<JobInfo> good = tools.FetchFile(
            "https://example.test/a.bin", "work:dl\\a.bin", Convert.ToHexString(SHA256.HashData(payload)));
        JobInfo goodDone = (await _jobManager.WaitAsync(good.Value!.Id, TimeSpan.FromSeconds(30), default)).Value!;
        OperationResult<JobInfo> bad = tools.FetchFile("https://example.test/b.bin", "work:dl\\b.bin", new string('0', 64));
        JobInfo badDone = (await _jobManager.WaitAsync(bad.Value!.Id, TimeSpan.FromSeconds(30), default)).Value!;

        Assert.Equal(JobState.Succeeded, goodDone.State);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "dl", "a.bin")));
        Assert.Equal(JobState.Failed, badDone.State);
        Assert.False(File.Exists(Path.Combine(_root, "dl", "b.bin")));
    }

    [Fact]
    public async Task FetchFile_CopiesFromUncPathShapedSource()
    {
        string share = Directory.CreateTempSubdirectory("vestigo-share-").FullName;
        try
        {
            string source = Path.Combine(share, "s.bin");
            File.WriteAllText(source, "from share");
            // A local admin share gives a real UNC path without any network setup.
            string unc = @"\\localhost\" + source[0] + "$" + source[2..];
            if (!File.Exists(unc))
            {
                return;
            }

            OperationResult<JobInfo> job = Tools().FetchFile(unc, "work:s.bin");
            JobInfo done = (await _jobManager.WaitAsync(job.Value!.Id, TimeSpan.FromSeconds(30), default)).Value!;

            Assert.Equal(JobState.Succeeded, done.State);
            Assert.Equal("from share", File.ReadAllText(Path.Combine(_root, "s.bin")));
        }
        finally
        {
            Directory.Delete(share, recursive: true);
        }
    }

    [Fact]
    public void FetchFile_ValidatesSourceAndDestination()
    {
        FileTools tools = Tools();

        Assert.Equal("invalid_source", tools.FetchFile("ftp://x/y", "work:y").ErrorCode);
        Assert.Equal("invalid_source", tools.FetchFile("relative.txt", "work:y").ErrorCode);
        Assert.Equal("invalid_source", tools.FetchFile(@"\\?\C:\Windows\notepad.exe", "work:y").ErrorCode);
        Assert.Equal("invalid_source", tools.FetchFile(@"\\.\C:\Windows\notepad.exe", "work:y").ErrorCode);
        Assert.Equal("access_denied", tools.FetchFile("https://x/y", "ro:y").ErrorCode);
    }

    private sealed class StubClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(handler));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

[Collection("VestigoHost")]
public class FilesEndpointTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-files-host").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("vestigo-files-work").FullName;
    private readonly string _readonly = Directory.CreateTempSubdirectory("vestigo-files-ro").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public FilesEndpointTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        foreach (string directory in new[] { _settings, _work, _readonly })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup of temp data.
            }
        }
    }

    private WebApplicationFactory<Program> Create(string[] tokens, long maxUpload = 1024 * 1024)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string json = JsonSerializer.Serialize(new
        {
            Vestigo = new
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                ApiTokens = tokens,
                Files = new { MaxUploadBytes = maxUpload },
                Roots = new[]
                {
                    new { Name = "work", Path = _work, Access = "Read, Write" },
                    new { Name = "ro", Path = _readonly, Access = "Read" }
                }
            }
        });
        File.WriteAllText(Path.Combine(_settings, "appsettings.json"), json);
        return new WebApplicationFactory<Program>();
    }

    private static ByteArrayContent Body(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Put_StoresFileAndReportsMetadata()
    {
        await using WebApplicationFactory<Program> factory = Create([]);
        using HttpClient client = factory.CreateClient();
        ByteArrayContent body = Body("hello world");
        body.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData("hello world"u8)));

        HttpResponseMessage response = await client.PutAsync("/files/work/bin/x.sys", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Path.Combine(_work, "bin", "x.sys"), json.RootElement.GetProperty("fullPath").GetString());
        Assert.Equal(11, json.RootElement.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData("hello world"u8)), json.RootElement.GetProperty("sha256").GetString());
        Assert.False(json.RootElement.GetProperty("replacedExisting").GetBoolean());
        Assert.False(json.RootElement.GetProperty("rebootRequired").GetBoolean());
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(_work, "bin", "x.sys")));
    }

    [Fact]
    public async Task Put_HonorsOverwriteAndHashHeader()
    {
        await using WebApplicationFactory<Program> factory = Create([]);
        using HttpClient client = factory.CreateClient();
        await client.PutAsync("/files/work/a.bin", Body("one"));

        HttpResponseMessage conflict = await client.PutAsync("/files/work/a.bin", Body("two"));
        HttpResponseMessage replaced = await client.PutAsync("/files/work/a.bin?overwrite=true", Body("two"));
        ByteArrayContent wrong = Body("three");
        wrong.Headers.Add("X-Content-SHA256", new string('A', 64));
        HttpResponseMessage mismatch = await client.PutAsync("/files/work/a.bin?overwrite=true", wrong);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.True(JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()).RootElement.GetProperty("replacedExisting").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("two", File.ReadAllText(Path.Combine(_work, "a.bin")));
    }

    [Fact]
    public async Task Put_EnforcesRootsAccessAndSizeLimit()
    {
        await using WebApplicationFactory<Program> factory = Create([], maxUpload: 8);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage readOnly = await client.PutAsync("/files/ro/a.bin", Body("x"));
        HttpResponseMessage unknown = await client.PutAsync("/files/zzz/a.bin", Body("x"));
        HttpResponseMessage traversal = await client.PutAsync("/files/work/%2e%2e%2foutside.bin", Body("x"));
        HttpResponseMessage stream = await client.PutAsync("/files/work/a.bin:evil", Body("x"));
        HttpResponseMessage tooLarge = await client.PutAsync("/files/work/big.bin", Body("0123456789"));
        HttpResponseMessage chunked = await client.PutAsync("/files/work/big2.bin", new StreamContent(new ThrottledStream("0123456789"u8.ToArray())));

        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        // Encoded separators are not decoded by the server, so the name stays one segment inside the root.
        Assert.True(traversal.StatusCode == HttpStatusCode.BadRequest || (await traversal.Content.ReadAsStringAsync()).Contains(Path.GetFileName(_work), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, stream.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.False(File.Exists(Path.Combine(_work, "big.bin")));
        Assert.False(File.Exists(Path.Combine(_work, "big2.bin")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_work)!, "outside.bin")));
    }

    [Fact]
    public async Task Get_StreamsFileWithRangeSupport()
    {
        File.WriteAllText(Path.Combine(_work, "r.txt"), "0123456789");
        File.WriteAllText(Path.Combine(_readonly, "ro.txt"), "readable");
        await using WebApplicationFactory<Program> factory = Create([]);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage full = await client.GetAsync("/files/work/r.txt");
        HttpRequestMessage ranged = new(HttpMethod.Get, "/files/work/r.txt");
        ranged.Headers.Range = new RangeHeaderValue(2, 5);
        HttpResponseMessage partial = await client.SendAsync(ranged);
        HttpResponseMessage fromReadOnly = await client.GetAsync("/files/ro/ro.txt");
        HttpResponseMessage missing = await client.GetAsync("/files/work/none.txt");

        Assert.Equal("0123456789", await full.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("2345", await partial.Content.ReadAsStringAsync());
        Assert.Equal("readable", await fromReadOnly.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RequireTheSameAuthAsMcp()
    {
        File.WriteAllText(Path.Combine(_work, "r.txt"), "x");
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage anonymousGet = await client.GetAsync("/files/work/r.txt");
        HttpResponseMessage anonymousPut = await client.PutAsync("/files/work/n.txt", Body("x"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage authorizedGet = await client.GetAsync("/files/work/r.txt");
        HttpResponseMessage authorizedPut = await client.PutAsync("/files/work/n.txt", Body("x"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPut.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorizedGet.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorizedPut.StatusCode);
    }

    [Fact]
    public async Task TicketedGet_WorksOnceWithoutApiToken()
    {
        string file = Path.Combine(_work, "shot.png");
        File.WriteAllText(file, "0123456789");
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();
        FileTicketStore store = factory.Services.GetRequiredService<FileTicketStore>();
        string ticket = store.Issue("GET", file).Token;

        HttpResponseMessage wrongPath = await client.GetAsync($"/files/work/other.png?ticket={ticket}");
        HttpResponseMessage first = await client.GetAsync($"/files/work/shot.png?ticket={ticket}");
        HttpResponseMessage second = await client.GetAsync($"/files/work/shot.png?ticket={ticket}");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPath.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("0123456789", await first.Content.ReadAsStringAsync());
        Assert.Contains("no-store", first.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains("invalid_ticket", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticket_IsRejectedForWrongMethod()
    {
        string file = Path.Combine(_work, "m.txt");
        File.WriteAllText(file, "x");
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();
        FileTicketStore store = factory.Services.GetRequiredService<FileTicketStore>();
        string ticket = store.Issue("GET", file).Token;

        HttpResponseMessage put = await client.PutAsync($"/files/work/m.txt?overwrite=true&ticket={ticket}", Body("y"));
        HttpResponseMessage get = await client.GetAsync($"/files/work/m.txt?ticket={ticket}");

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal("x", File.ReadAllText(file));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task TicketedPut_UsesBoundFlagsAndIsConsumedOnSuccess()
    {
        string file = Path.Combine(_work, "up.bin");
        File.WriteAllText(file, "old");
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();
        FileTicketStore store = factory.Services.GetRequiredService<FileTicketStore>();
        string noOverwrite = store.Issue("PUT", file, overwrite: false).Token;
        string overwrite = store.Issue("PUT", file, overwrite: true).Token;

        // The query string cannot widen what the ticket allows. A failed upload leaves the ticket usable.
        HttpResponseMessage conflict = await client.PutAsync($"/files/work/up.bin?overwrite=true&ticket={noOverwrite}", Body("new"));
        HttpResponseMessage ok = await client.PutAsync($"/files/work/up.bin?ticket={overwrite}", Body("new"));
        HttpResponseMessage reused = await client.PutAsync($"/files/work/up.bin?ticket={overwrite}", Body("again"));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public async Task Ticket_GarbageIsRejectedWithoutRevealingPathState()
    {
        File.WriteAllText(Path.Combine(_work, "g.txt"), "x");
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage garbage = await client.GetAsync("/files/work/g.txt?ticket=nonsense");
        HttpResponseMessage missingFile = await client.GetAsync("/files/work/none.txt?ticket=nonsense");

        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missingFile.StatusCode);
    }

    /// <summary>Content without a known length, so the request is chunked.</summary>
    private sealed class ThrottledStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
