using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class CrashDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-crash").FullName;
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-01-02T03:04:05Z");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A dump file still open on Windows can fail the cleanup. The next run uses a new directory.
        }
    }

    [Fact]
    public void Parser_ReadsUtf16MetadataAndCapsValues()
    {
        long fileTime = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        string body = "EventType=APPCRASH\r\n"
            + $"EventTime={fileTime}\r\n"
            + "AppName=notepad.exe\r\n"
            + "AppPath=C:\\Windows\\notepad.exe\r\n"
            + "ReportIdentifier={abc}\r\n"
            + "Sig[0].Name=Application Name\r\n"
            + "Sig[0].Value=" + new string('v', 600) + "\r\n"
            + "DynamicSig[1].Name=OS Version\r\n"
            + "DynamicSig[1].Value=10.0\r\n";
        byte[] encoded = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(body)).ToArray();

        ParsedWerReport parsed = WerReportParser.Parse(encoded, truncated: false);

        Assert.Equal("APPCRASH", parsed.EventType);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), parsed.EventTime);
        Assert.Equal("notepad.exe", parsed.AppName);
        Assert.Equal("{abc}", parsed.ReportId);
        Assert.Equal(2, parsed.Signatures.Count);
        Assert.Equal(512, parsed.Signatures[0].Value.Length);
        Assert.False(parsed.Truncated);
    }

    [Fact]
    public void Parser_KeepsStaticAndDynamicSignaturesWithTheSameIndex()
    {
        ParsedWerReport parsed = WerReportParser.Parse(
            "DynamicSig[1].Name=OS Version\nDynamicSig[1].Value=dynamic\nSig[1].Name=Application Name\nSig[1].Value=static\n"u8,
            truncated: false);

        Assert.Equal(["Application Name", "OS Version"], parsed.Signatures.Select(signature => signature.Name));
        Assert.Equal(["static", "dynamic"], parsed.Signatures.Select(signature => signature.Value));
    }

    [Fact]
    public void Parser_RejectsAnOutOfRangeEventTime()
    {
        ParsedWerReport parsed = WerReportParser.Parse("EventType=APPCRASH\nEventTime=9999999999999999999\n"u8, truncated: true);

        Assert.Equal("APPCRASH", parsed.EventType);
        Assert.Null(parsed.EventTime);
        Assert.True(parsed.Truncated);
    }

    [Fact]
    public void List_ReturnsMetadataAndSkipsDumpBytesLinksAndNearMisses()
    {
        string archive = Path.Combine(_root, "archive");
        string good = Path.Combine(archive, "AppCrash_good");
        Directory.CreateDirectory(good);
        long fileTime = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        File.WriteAllText(Path.Combine(good, "Report.wer"), $"EventType=APPCRASH\nEventTime={fileTime}\nAppName=notepad.exe\nAppPath=C:\\Windows\\notepad.exe\n");
        File.WriteAllText(Path.Combine(good, "crash.dmp"), "VESTIGO-DUMP-SECRET");
        Directory.CreateDirectory(Path.Combine(archive, "NotAReport"));
        File.WriteAllText(Path.Combine(archive, "NotAReport", "Report.wer.txt"), "EventType=SKIP\n");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Report.wer"), "EventType=LINKED\nAppName=UNIQUE-OUTSIDE\n");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(archive, "linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links are not available.");
        }
        string user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, "AppCrash_user"));
        File.WriteAllText(Path.Combine(user, "AppCrash_user", "Report.wer"), $"EventType=APPCRASH\nEventTime={fileTime + 1}\nAppName=user.exe\n");
        Directory.CreateDirectory(Path.Combine(archive, "broken", "Report.wer"));

        WerCrashReportSource source = new(
            () =>
            [
                new WerStore("machine", "archive", archive, null),
                new WerStore("user", "archive", user, "Ada")
            ],
            new DiagnosticsOptions { MaxCrashReportFileBytes = 256 * 1024 });

        OperationResult<CrashReportList> listed = source.List("all", 10, null);
        string json = JsonSerializer.Serialize(listed.Value);

        Assert.True(listed.Success);
        Assert.Equal(2, listed.Value!.Count);
        Assert.DoesNotContain("VESTIGO-DUMP-SECRET", json);
        Assert.DoesNotContain("UNIQUE-OUTSIDE", json);
        Assert.DoesNotContain("SKIP", json);
        Assert.Contains(listed.Value.Reports, report => report.DumpFiles.Count == 1 && report.DumpFiles[0].Bytes == "VESTIGO-DUMP-SECRET".Length);
        Assert.True(listed.Value.Partial);
        Assert.True(listed.Value.Skipped >= 1);
        Assert.Equal("user.exe", listed.Value.Reports[0].AppName);
        Assert.Equal("Ada", listed.Value.Reports[0].Profile);

        OperationResult<CrashReportList> machine = source.List("machine", 10, DateTimeOffset.Parse("2024-07-01T00:00:00Z"));
        Assert.Equal(0, machine.Value!.Count);
    }

    [Fact]
    public void List_MissingStoreIsQuietAndRejectedStoresArePartial()
    {
        string missing = Path.Combine(_root, "missing-store");
        WerCrashReportSource quiet = new(() => [new WerStore("machine", "archive", missing, null)], new DiagnosticsOptions());
        OperationResult<CrashReportList> missingResult = quiet.List("all", 10, null);
        Assert.False(missingResult.Value!.Partial);
        Assert.Equal(0, missingResult.Value.Skipped);
        Assert.Equal(0, missingResult.Value.Count);

        string file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "x");
        OperationResult<CrashReportList> blocked = new WerCrashReportSource(
            () => [new WerStore("machine", "archive", file, null)],
            new DiagnosticsOptions()).List("all", 10, null);
        Assert.True(blocked.Value!.Partial);
        Assert.True(blocked.Value.Skipped >= 1);
        Assert.Equal(0, blocked.Value.Count);

        if (!OperatingSystem.IsWindows())
        {
            string locked = Path.Combine(_root, "locked-store");
            Directory.CreateDirectory(locked);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            try
            {
                OperationResult<CrashReportList> denied = new WerCrashReportSource(
                    () => [new WerStore("machine", "archive", locked, null)],
                    new DiagnosticsOptions()).List("all", 10, null);
                Assert.True(denied.Value!.Partial);
                Assert.Equal(0, denied.Value.Count);
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        string outside = Path.Combine(_root, "linked-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Report.wer"), "EventType=APPCRASH\nAppName=UNIQUE-LINKED-STORE\n");
        string link = Path.Combine(_root, "linked-store");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        OperationResult<CrashReportList> linked = new WerCrashReportSource(
            () => [new WerStore("machine", "archive", link, null)],
            new DiagnosticsOptions()).List("all", 10, null);
        Assert.True(linked.Value!.Partial);
        Assert.Equal(0, linked.Value.Count);
        Assert.DoesNotContain("UNIQUE-LINKED-STORE", JsonSerializer.Serialize(linked.Value));
    }

    [Fact]
    public void Capture_RefusesUntilTheGateIsOnAndDoesNotCallTheWriter()
    {
        FakeDumps dumps = new();
        CrashTools tools = Tools(dumps, free: 4096, allow: false);

        OperationResult<ProcessDumpResult> result = tools.CaptureProcessDump(10, "out:note.dmp", Started).Result;

        Assert.Equal("process_dumps_disabled", result.ErrorCode);
        Assert.False(dumps.InspectCalled);
    }

    [Fact]
    public void Capture_RefusesProtectedProcessesMismatchedIdentityAndTightDisks()
    {
        FakeDumps dumps = new() { Target = new ProcessDumpTarget(10, "lsass", Started, true) };
        CrashTools tools = Tools(dumps, free: 4096, allow: true);
        Assert.Equal("protected_process", tools.CaptureProcessDump(10, "out:note.dmp", Started).Result.ErrorCode);

        dumps.Target = new ProcessDumpTarget(10, "notepad", Started, false);
        Assert.Equal("process_identity_mismatch", tools.CaptureProcessDump(10, "out:note.dmp", Started.AddSeconds(1)).Result.ErrorCode);

        CrashTools tight = Tools(dumps, free: 100, allow: true);
        Assert.Equal("insufficient_disk_space", tight.CaptureProcessDump(10, "out:note.dmp", Started).Result.ErrorCode);
        Assert.False(dumps.WriteCalled);
    }

    [Fact]
    public void Capture_WritesADumpJobWithoutReturningDumpBytes()
    {
        FakeDumps dumps = new();
        RecordingLogger logger = new();
        CrashTools tools = Tools(dumps, free: 4096, allow: true, logger);

        OperationResult<ProcessDumpResult> result = tools.CaptureProcessDump(10, "out:note.dmp", Started, dumpType: "FULL", wait: 5).Result;

        Assert.True(result.Success);
        Assert.Equal("full", result.Value!.DumpType);
        Assert.Equal("out:note.dmp", result.Value.Output);
        Assert.False(result.Value.StillRunning);
        Assert.Equal(JobState.Succeeded, result.Value.Job.State);
        Assert.Contains("bytes", result.Value.OutputTail);
        Assert.DoesNotContain("VESTIGO-DUMP-SECRET", result.Value.OutputTail);
        Assert.Equal("VESTIGO-DUMP-SECRET", File.ReadAllText(Path.Combine(_root, "note.dmp")));
        Assert.All(logger.Lines, line => Assert.DoesNotContain("VESTIGO-DUMP-SECRET", line));
        Assert.All(logger.Lines, line => Assert.DoesNotContain(_root, line));
    }

    [Fact]
    public void Capture_RejectsAnExistingFileAndAPathOutsideTheRoots()
    {
        File.WriteAllText(Path.Combine(_root, "note.dmp"), "present");
        FakeDumps dumps = new();
        CrashTools tools = Tools(dumps, free: 4096, allow: true);

        Assert.Equal("already_exists", tools.CaptureProcessDump(10, "out:note.dmp", Started).Result.ErrorCode);
        Assert.Equal("invalid_request", tools.CaptureProcessDump(10, "out:note.txt", Started).Result.ErrorCode);
        Assert.False(dumps.WriteCalled);
        OperationResult<ProcessDumpResult> outside = tools.CaptureProcessDump(10, Path.Combine(_root, "..", "elsewhere.dmp"), Started).Result;
        Assert.False(outside.Success);
        Assert.False(dumps.WriteCalled);
    }

    [Fact]
    public void RefusalList_BlocksCriticalProcessesByName()
    {
        Assert.True(ProcessDumpPolicy.IsRefused("lsass.exe"));
        Assert.True(ProcessDumpPolicy.IsRefused("Secure System"));
        Assert.False(ProcessDumpPolicy.IsRefused("notepad.exe"));
    }

    [Fact]
    public void DumpFileBinder_NameCollisionIsFileExists()
    {
        Assert.Equal(80, DumpFileBinder.ErrorForStatus(unchecked((int)0xC0000035)));
    }

    [Fact]
    public void ReadBounded_FillsTheBufferAcrossShortReads()
    {
        byte[] data = "EventType=APPCRASH\nExtra=1\n"u8.ToArray();
        byte[] buffer = new byte[12];

        (int read, bool truncated) = WerCrashReportSource.ReadBounded(new OneByteStream(data), buffer);

        Assert.Equal(12, read);
        Assert.True(truncated);
        Assert.Equal(data.AsSpan(0, 12).ToArray(), buffer[..read]);
        (int full, bool more) = WerCrashReportSource.ReadBounded(new OneByteStream(data), new byte[data.Length]);
        Assert.Equal(data.Length, full);
        Assert.False(more);
    }

    [Fact]
    public async Task MiniDumpWriter_VerySmallLimitReturnsDumpTooLarge()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Process dumps require Windows.");
        }

        using Process child = new();
        child.StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c ping 127.0.0.1 -n 30",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Assert.True(child.Start());
        try
        {
            string path = Path.Combine(_root, "tiny.dmp");
            OperationResult<long> result = await new MiniDumpWriter().WriteAsync(
                new ProcessDumpRequest(child.Id, child.StartTime, path, "mini", 1, _root),
                CancellationToken.None);

            Assert.Equal("dump_too_large", result.ErrorCode);
            Assert.False(File.Exists(path));
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    [Fact]
    public void MiniDumpWriter_RequiresWindowsOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("windows_required", new MiniDumpWriter().Inspect(1).ErrorCode);
    }

    [Fact]
    public void Discover_DoesNotThrow()
    {
        Assert.NotNull(WindowsWerLocations.Discover());
    }

    private CrashTools Tools(FakeDumps dumps, long? free, bool allow, RecordingLogger? logger = null)
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "out", Path = _root, Access = RootAccess.Read | RootAccess.Write }],
            Diagnostics =
            {
                AllowProcessDumps = allow,
                MaxDumpBytes = 1024,
                MinDumpFreeBytes = 1024
            }
        };
        return new CrashTools(
            new WerCrashReportSource(() => [], options.Diagnostics),
            dumps,
            new FakeSpace(free),
            new RootPolicy(options),
            new JobManager(options, Path.Combine(_root, "jobs")),
            options,
            logger ?? new RecordingLogger());
    }

    private sealed class FakeDumps : IProcessDumpWriter
    {
        public bool InspectCalled { get; private set; }

        public bool WriteCalled { get; private set; }

        public ProcessDumpTarget Target { get; set; } = new(10, "notepad", Started, false);

        public OperationResult<ProcessDumpTarget> Inspect(int pid)
        {
            InspectCalled = true;
            return OperationResult<ProcessDumpTarget>.Ok(Target with { Pid = pid });
        }

        public Task<OperationResult<long>> WriteAsync(ProcessDumpRequest request, CancellationToken cancellationToken)
        {
            WriteCalled = true;
            File.WriteAllText(request.FullPath, "VESTIGO-DUMP-SECRET");
            return Task.FromResult(OperationResult<long>.Ok(new FileInfo(request.FullPath).Length));
        }
    }

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, count > 1 ? 1 : count);
    }

    private sealed class FakeSpace(long? free) : IVolumeSpace
    {
        public long? FreeBytes(string directory) => free;
    }

    private sealed class RecordingLogger : ILogger<CrashTools>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
