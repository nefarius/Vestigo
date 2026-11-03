using System.Text;

using Vestigo.Configuration;
using Vestigo.Diagnostics;

namespace Vestigo.Server.Tests;

public sealed class SetupApiLogTests : IDisposable
{
    private const string Id = @"ROOT\VESTIGOTEST\0000";
    private readonly string _directory = Directory.CreateTempSubdirectory("vestigo-setupapi").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Parser_MatchesTheHeaderTokenAndKeepsOriginalCodes()
    {
        string secret = "SECRET-RAW-LINE";
        string[] lines =
        [
            $">>>  [Device Install (Hardware initiated) - {Id}A]",
            ">>>  Section start 2024/03/01 10:00:00.000",
            $"     dvi: Hardware ID {Id}",
            "<<<  Section end 2024/03/01 10:00:01.000",
            "<<<  [Exit status: SUCCESS]",
            $">>>  [Device Install (Hardware initiated) - {Id}]",
            ">>>  Section start 2024/03/01 11:11:12.123",
            @"     dvi: Selected driver installs from section [Standard] in 'c:\windows\inf\oem7.inf'.",
            "     inf: DriverVer=01/02/2024,1.2.3.4",
            secret,
            "<<<  Section end 2024/03/01 11:11:13.456",
            "<<<  [Exit status: FAILURE(0xE0000228)]"
        ];

        IReadOnlyList<InstallSection> sections = SetupApiSectionParser.Parse(lines, "setupapi.dev.log", Id, includeRaw: false);

        InstallSection section = Assert.Single(sections);
        Assert.Equal("FAILURE(0xE0000228)", section.ExitStatus);
        Assert.Contains("0xE0000228", section.NumericCodes);
        Assert.Contains(section.InfLines, line => line.Contains("oem7.inf", StringComparison.Ordinal));
        Assert.Contains(section.DriverVersionLines, line => line.Contains("1.2.3.4", StringComparison.Ordinal));
        Assert.Equal("setupapi.dev.log", section.SourceFile);
        Assert.True(section.Complete);
        Assert.Null(section.Lines);
        Assert.Equal(11, section.StartedAt!.Value.Hour);
        Assert.Equal(2024, section.StartedAt.Value.Year);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(section), StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_MarksAnUnterminatedSectionIncomplete()
    {
        string[] lines =
        [
            $">>>  [Device Install - {Id}]",
            ">>>  Section start 2024/03/01 11:11:12.123",
            "     dvi: still running"
        ];

        InstallSection section = Assert.Single(SetupApiSectionParser.Parse(lines, "setupapi.dev.log", Id, includeRaw: true));

        Assert.False(section.Complete);
        Assert.Null(section.ExitStatus);
        Assert.NotNull(section.Lines);
    }

    [Fact]
    public void Reader_OrdersRotatedFilesAndSkipsBytesPastTheCap()
    {
        Write("setupapi.dev.20240301_100000.log", Section(Id, "2024/03/01 10:00:00.000", "SUCCESS", "OLD-ONLY"));
        Write("setupapi.dev.log", Section(Id, "2024/03/01 12:00:00.000", "FAILURE(0x2)", "NEW-ONLY"));
        SetupApiLogReader reader = new(_directory);

        OperationResult<InstallHistory> history = reader.Read(Id, null, 20, false, 1024 * 1024);

        Assert.True(history.Success, history.Error);
        Assert.Equal(2, history.Value!.Sections.Count);
        Assert.Contains("OLD-ONLY", history.Value.Sections[0].InfLines[0], StringComparison.Ordinal);
        Assert.Contains("NEW-ONLY", history.Value.Sections[1].InfLines[0], StringComparison.Ordinal);
        Assert.False(history.Value.Truncated);

        OperationResult<InstallHistory> capped = reader.Read(Id, null, 20, false, 1);
        Assert.True(capped.Success, capped.Error);
        Assert.True(capped.Value!.Truncated);
        Assert.DoesNotContain(capped.Value.Sections, section => section.InfLines.Any(line => line.Contains("OLD-ONLY", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reader_AppliesSinceAndLimitWithoutACallerPath()
    {
        Write("setupapi.dev.log", Section(Id, "2024/03/01 08:00:00.000", "SUCCESS", "early") + Section(Id, "2024/03/01 18:00:00.000", "SUCCESS", "late"));
        InstallHistoryTools tools = new(new SetupApiLogReader(_directory), new VestigoOptions());

        OperationResult<InstallHistory> history = tools.GetDeviceInstallHistory(Id, since: "1990-01-01T00:00:00Z", limit: 1);
        OperationResult<InstallHistory> future = tools.GetDeviceInstallHistory(Id, since: "2999-01-01T00:00:00Z");

        Assert.True(history.Success, history.Error);
        Assert.Single(history.Value!.Sections);
        Assert.Contains("late", history.Value.Sections[0].InfLines[0], StringComparison.Ordinal);
        Assert.True(future.Success, future.Error);
        Assert.Empty(future.Value!.Sections);
        Assert.Equal("invalid_request", tools.GetDeviceInstallHistory(" ").ErrorCode);
        Assert.Equal("invalid_time", tools.GetDeviceInstallHistory(Id, since: "yesterday").ErrorCode);
    }

    [Fact]
    public void Reader_AlignsAnOddUtf16SliceToACodeUnit()
    {
        string path = Path.Combine(_directory, "setupapi.dev.log");
        File.WriteAllText(path, "pad\n" + Section(Id, "2024/03/01 12:00:00.000", "SUCCESS", "ALIGNED"), Encoding.Unicode);
        long length = new FileInfo(path).Length;

        OperationResult<InstallHistory> history = new SetupApiLogReader(_directory).Read(Id, null, 20, false, length - 1);

        Assert.True(history.Success, history.Error);
        Assert.Contains(history.Value!.Sections, section => section.InfLines.Any(line => line.Contains("ALIGNED", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reader_StopsAGrowingStreamAtTheCapturedByteCount()
    {
        IReadOnlyList<string> lines = SetupApiLogReader.ReadLines(new EndlessStream(), maxBytes: 16, Encoding.UTF8);

        Assert.NotEmpty(lines);
        Assert.True(Encoding.UTF8.GetByteCount(string.Join('\n', lines)) <= 16 + lines[^1].Length);
        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 16));
    }

    [Fact]
    public void Reader_MissingDirectoryIsAnIoError()
    {
        OperationResult<InstallHistory> history = new SetupApiLogReader(Path.Combine(_directory, "missing")).Read(Id, null, 20, false, 1024);

        Assert.Equal("io_error", history.ErrorCode);
    }

    private void Write(string name, string text) =>
        File.WriteAllText(Path.Combine(_directory, name), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Section(string id, string start, string status, string marker) =>
        $">>>  [Device Install - {id}]\n>>>  Section start {start}\n     dvi: {marker}.inf\n<<<  Section end {start}\n<<<  [Exit status: {status}]\n";

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'A', offset, count);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
