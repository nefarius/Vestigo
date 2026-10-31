using System.Text;

using Vestigo.Configuration;
using Vestigo.EventLogs;
using Vestigo.LogFiles;
using Vestigo.Modules;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class LogsAndEventLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-logs-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MemoryStream Growable(byte[] bytes)
    {
        MemoryStream stream = new();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Utf8(string text) => Growable(new UTF8Encoding(false).GetBytes(text));

    private static MemoryStream WithBom(Encoding encoding, string text) =>
        Growable([.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);

    [Theory]
    [InlineData("a\nb\nc\n")]
    [InlineData("a\r\nb\r\nc\r\n")]
    [InlineData("a\nb\nc")]
    public void Tail_ReturnsLastLinesForLfCrlfAndMissingTerminator(string text)
    {
        TextSlice slice = TextTailer.Tail(Utf8(text), 7, 2, 1024);

        Assert.Equal(["b", "c"], slice.Lines);
        Assert.True(slice.Truncated);
        Assert.Equal(new TextCursor(text.Length, text.Length, 7), slice.Cursor);
    }

    [Fact]
    public void Tail_EmptyFileHasNoLines()
    {
        TextSlice slice = TextTailer.Tail(new MemoryStream(), 1, 10, 1024);

        Assert.Empty(slice.Lines);
        Assert.False(slice.Truncated);
        Assert.Equal(0, slice.Cursor.Offset);
    }

    [Fact]
    public void Tail_ReturnsEverythingWhenFileIsSmall()
    {
        TextSlice slice = TextTailer.Tail(Utf8("one\ntwo\n"), 1, 10, 1024);

        Assert.Equal(["one", "two"], slice.Lines);
        Assert.False(slice.Truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tail_DecodesUtf16WithBom(bool bigEndian)
    {
        Encoding encoding = new UnicodeEncoding(bigEndian, true);
        TextSlice slice = TextTailer.Tail(WithBom(encoding, "alpha\r\nbeta \u00e4\u00f6\r\ngamma\r\n"), 1, 2, 4096);

        Assert.Equal(["beta \u00e4\u00f6", "gamma"], slice.Lines);
        Assert.Equal(bigEndian ? "utf-16be" : "utf-16le", slice.Encoding);
    }

    [Fact]
    public void Tail_DetectsUtf8BomAndDecodesMultibyteAtWindowStart()
    {
        string text = "\u00e4\u00e4\u00e4\u00e4\nlast\n";
        TextSlice slice = TextTailer.Tail(WithBom(new UTF8Encoding(true), text), 1, 10, 9);

        Assert.Equal(["last"], slice.Lines);
        Assert.True(slice.Truncated);
        Assert.Equal("utf-8", slice.Encoding);

        TextSlice whole = TextTailer.Tail(WithBom(new UTF8Encoding(true), text), 1, 10, 4096);
        Assert.Equal(["\u00e4\u00e4\u00e4\u00e4", "last"], whole.Lines);
    }

    [Fact]
    public void Tail_HugeSingleLineIsCappedToItsEndAndMarkedTruncated()
    {
        string text = new string('x', 1000) + "END";
        TextSlice slice = TextTailer.Tail(Utf8(text), 1, 10, 100);

        Assert.True(slice.Truncated);
        Assert.Single(slice.Lines);
        Assert.Equal(100, slice.Lines[0].Length);
        Assert.EndsWith("END", slice.Lines[0]);
    }

    [Fact]
    public void Tail_KeepsFirstLineWhenWindowStartsOnBoundary()
    {
        // The 4 byte window starts right after the newline, so "def" is complete.
        TextSlice slice = TextTailer.Tail(Utf8("abc\ndef\n"), 1, 10, 4);

        Assert.Equal(["def"], slice.Lines);
    }

    [Fact]
    public void ReadSince_ReturnsOnlyNewCompleteLines()
    {
        MemoryStream stream = Utf8("one\ntwo\npart");
        TextSlice first = TextTailer.ReadSince(stream, 5, new TextCursor(0, 0, 5), 100, 1024);

        Assert.Equal(["one", "two"], first.Lines);
        Assert.Equal(8, first.Cursor.Offset);
        Assert.False(first.Rotated);

        stream.Seek(0, SeekOrigin.End);
        stream.Write("ial\nnext\n"u8);
        TextSlice second = TextTailer.ReadSince(stream, 5, first.Cursor, 100, 1024);

        Assert.Equal(["partial", "next"], second.Lines);
        Assert.Equal(stream.Length, second.Cursor.Offset);

        TextSlice none = TextTailer.ReadSince(stream, 5, second.Cursor, 100, 1024);
        Assert.Empty(none.Lines);
        Assert.False(none.HasMore);
    }

    [Fact]
    public void ReadSince_PagesByLineLimit()
    {
        MemoryStream stream = Utf8("1\n2\n3\n4\n5\n");
        TextSlice page = TextTailer.ReadSince(stream, 1, new TextCursor(0, 0, 1), 2, 1024);

        Assert.Equal(["1", "2"], page.Lines);
        Assert.True(page.HasMore);
        TextSlice rest = TextTailer.ReadSince(stream, 1, page.Cursor, 10, 1024);
        Assert.Equal(["3", "4", "5"], rest.Lines);
        Assert.False(rest.HasMore);
    }

    [Fact]
    public void ReadSince_DetectsTruncationAndRestartsFromZero()
    {
        TextSlice slice = TextTailer.ReadSince(Utf8("new\n"), 1, new TextCursor(500, 500, 1), 10, 1024);

        Assert.True(slice.Rotated);
        Assert.Equal(["new"], slice.Lines);
    }

    [Fact]
    public void ReadSince_DetectsReplacedFileByIdentity()
    {
        TextSlice slice = TextTailer.ReadSince(Utf8("fresh start line here\n"), 2, new TextCursor(4, 4, 1), 10, 1024);

        Assert.True(slice.Rotated);
        Assert.Equal(["fresh start line here"], slice.Lines);
    }

    [Fact]
    public void ReadSince_HugeLineIsDeliveredInPieces()
    {
        MemoryStream stream = Utf8(new string('y', 100) + "\n");
        TextSlice first = TextTailer.ReadSince(stream, 1, new TextCursor(0, 0, 1), 10, 40);

        Assert.True(first.Truncated);
        Assert.Equal(40, first.Lines[0].Length);
        Assert.Equal(40, first.Cursor.Offset);
        Assert.True(first.HasMore);
    }

    [Fact]
    public void ReadSince_SkipsBomAndReadsUtf16()
    {
        Encoding encoding = new UnicodeEncoding(false, true);
        MemoryStream stream = WithBom(encoding, "a\r\nb\r\n");
        TextSlice slice = TextTailer.ReadSince(stream, 1, new TextCursor(0, 0, 1), 10, 1024);

        Assert.Equal(["a", "b"], slice.Lines);
        Assert.Equal(stream.Length, slice.Cursor.Offset);
    }

    [Fact]
    public void LogTools_ReadSharedFileUnderRootAndDetectRotation()
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "logs", Path = _root, Access = RootAccess.Read }]
        };
        LogTools tools = new(options, new RootPolicy(options));
        string file = Path.Combine(_root, "app.log");
        using (FileStream writer = new(file, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write("one\ntwo\nthree\n"u8);
            writer.Flush();

            OperationResult<TextSlice> tail = tools.TailTextFile("logs:app.log", 2);

            Assert.True(tail.Success);
            Assert.Equal(["two", "three"], tail.Value!.Lines);
        }

        TextCursor cursor = tools.TailTextFile("logs:app.log", 1).Value!.Cursor;
        File.WriteAllText(file, "x\n");
        OperationResult<TextSlice> since = tools.ReadTextFileSince("logs:app.log", cursor.Offset, cursor.Identity);

        Assert.True(since.Value!.Rotated);
        Assert.Equal(["x"], since.Value.Lines);
        Assert.Equal("not_found", tools.TailTextFile("logs:missing.log").ErrorCode);
        Assert.Equal("outside_root", tools.TailTextFile(@"logs:..\evil.log").ErrorCode);
    }

    [Fact]
    public void LogTools_ClampLineLimit()
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "logs", Path = _root, Access = RootAccess.Read }],
            Logs = new LogsOptions { MaxTailLines = 3 }
        };
        File.WriteAllText(Path.Combine(_root, "n.log"), "1\n2\n3\n4\n5\n");

        OperationResult<TextSlice> result = new LogTools(options, new RootPolicy(options)).TailTextFile("logs:n.log", 100);

        Assert.Equal(["3", "4", "5"], result.Value!.Lines);
    }

    [Fact]
    public void QueryBuilder_EmptyFilterSelectsEverything() =>
        Assert.Equal("*", EventLogQueryBuilder.Build(new EventLogFilter()));

    [Fact]
    public void QueryBuilder_CombinesFilters()
    {
        string query = EventLogQueryBuilder.Build(new EventLogFilter(
            ["Provider A", "B"],
            ["Error", "critical", "error"],
            [1000, 7036],
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)),
            new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            "*[System[(Provider[@Name='Provider A'] or Provider[@Name='B']) and (Level=1 or Level=2) and " +
            "(EventID=1000 or EventID=7036) and " +
            "TimeCreated[@SystemTime>='2026-01-02T02:04:05.000Z' and @SystemTime<='2026-01-03T00:00:00.000Z']]]",
            query);
    }

    [Fact]
    public void QueryBuilder_UsesSingleBoundOnly()
    {
        string query = EventLogQueryBuilder.Build(new EventLogFilter(Since: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("*[System[TimeCreated[@SystemTime>='2026-05-01T00:00:00.000Z']]]", query);
    }

    [Fact]
    public void QueryBuilder_EscapesQuotesByChoosingQuoteStyle()
    {
        Assert.Equal("*[System[(Provider[@Name=\"O'Neil\"])]]", EventLogQueryBuilder.Build(new EventLogFilter(["O'Neil"])));
        Assert.Equal("*[System[(Provider[@Name='say \"hi\"'])]]", EventLogQueryBuilder.Build(new EventLogFilter(["say \"hi\""])));
        Assert.Throws<ArgumentException>(() => EventLogQueryBuilder.Build(new EventLogFilter(["a'b\"c"])));
    }

    [Fact]
    public void QueryBuilder_RejectsInvalidInput()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => EventLogQueryBuilder.Build(new EventLogFilter(Levels: ["loud"])));
        Assert.Throws<ArgumentException>(() => EventLogQueryBuilder.Build(new EventLogFilter(EventIds: [-1])));
        Assert.Throws<ArgumentException>(() => EventLogQueryBuilder.Build(new EventLogFilter(Providers: [" "])));
        Assert.Throws<ArgumentException>(() => EventLogQueryBuilder.Build(new EventLogFilter(Since: now, Until: now.AddHours(-1))));
    }

    [Fact]
    public void QueryEventLog_ReadsOneEventFromApplicationLog()
    {
        VestigoOptions options = new();
        EventLogService service = new(options, new RootPolicy(options));

        OperationResult<EventQueryResult> result = service.Query(
            "Application", new EventLogFilter(), null, null, 1, true, true, default);

        if (!result.Success && result.ErrorCode == "access_denied")
        {
            return;
        }

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Events.Count <= 1);
        if (result.Value.Events.Count == 1)
        {
            Assert.False(string.IsNullOrEmpty(result.Value.Events[0].Provider));
            Assert.NotNull(result.Value.Events[0].Xml);
        }
    }

    [Fact]
    public void QueryEventLog_ValidatesInput()
    {
        VestigoOptions options = new();
        EventLogService service = new(options, new RootPolicy(options));

        Assert.Equal("invalid_channel", service.Query(" ", new EventLogFilter(), null, null, 1, true, false, default).ErrorCode);
        Assert.Equal("invalid_query", service.Query("Application", new EventLogFilter(EventIds: [1]), "*", null, 1, true, false, default).ErrorCode);
        Assert.Equal("not_found", service.Query("No-Such-Vestigo-Channel", new EventLogFilter(), null, null, 1, true, false, default).ErrorCode);
        Assert.Equal("roots_not_configured", service.Query(@"C:\x\a.evtx", new EventLogFilter(), null, null, 1, true, false, default).ErrorCode);
    }

    [Fact]
    public void ListEventLogs_IncludesApplicationChannel()
    {
        VestigoOptions options = new();
        EventLogService service = new(options, new RootPolicy(options));

        OperationResult<IReadOnlyList<EventLogSummary>> result = service.List("Application", includeCounts: false);

        if (!result.Success && result.ErrorCode == "access_denied")
        {
            return;
        }

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Value!, log => log.Name == "Application");
    }

    [Fact]
    public void Registry_ListsNewModulesAndOptionsValidate()
    {
        Assert.Contains(ModuleRegistry.All, module => module.Name == "Environment");
        Assert.Contains(ModuleRegistry.All, module => module.Name == "Logs");
        Assert.Contains(ModuleRegistry.All, module => module.Name == "EventLog");
        Assert.Equal(["COMSPEC", "SystemRoot", "windir"], new VestigoOptions().Environment.ProtectedNames);
        Assert.Equal(2000, new VestigoOptions().Logs.MaxTailLines);
        Assert.Equal(500, new VestigoOptions().EventLog.MaxEvents);
        Assert.Throws<InvalidOperationException>(new VestigoOptions { Logs = new LogsOptions { MaxTailLines = 0 } }.Validate);
        Assert.Throws<InvalidOperationException>(new VestigoOptions { EventLog = new EventLogOptions { MaxEvents = 0 } }.Validate);
    }
}
