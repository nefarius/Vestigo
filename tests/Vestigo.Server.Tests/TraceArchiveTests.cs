using System.IO.Compression;
using System.Text;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class TraceArchiveTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-archive").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public TraceArchiveTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.TraceDirectory);
    }

    [Fact]
    public void Extract_WritesAllowedEntriesAndSkipsOthers()
    {
        string zip = Path.Combine(VestigoPaths.TraceDirectory, "bundle.zip");
        CreateZip(zip, ("nested/driver.pdb", "pdb"), ("readme.txt", "skip"), ("trace.etl", "etl"));
        TraceArchiveExtractor extractor = CreateExtractor();

        OperationResult<TraceArchiveExtraction> result = extractor.Extract(zip, "traces:", overwrite: false);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.ExtractedPaths.Count);
        Assert.Contains(result.Value.SkippedEntries, entry => entry.Contains("readme.txt", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(VestigoPaths.TraceDirectory, "nested", "driver.pdb")));
    }

    [Fact]
    public void Extract_RejectsZipSlipAndAlternateStreams()
    {
        TraceArchiveExtractor extractor = CreateExtractor();

        Assert.Equal("unsafe_entry", ExtractUnsafe("..\\escape.pdb").ErrorCode);
        Assert.Equal("unsafe_entry", ExtractUnsafe("C:\\windows\\x.pdb").ErrorCode);
        Assert.Equal("unsafe_entry", ExtractUnsafe("nested:stream.pdb").ErrorCode);
    }

    [Fact]
    public void Extract_ValidatesAllEntriesBeforeWriting()
    {
        string destination = Path.Combine(VestigoPaths.TraceDirectory, "partial");
        Directory.CreateDirectory(destination);
        string zip = Path.Combine(VestigoPaths.TraceDirectory, "partial.zip");
        CreateZip(zip, ("good.pdb", "pdb"), ("..\\escape.pdb", "bad"));

        OperationResult<TraceArchiveExtraction> result =
            CreateExtractor().Extract(zip, destination, overwrite: false);

        Assert.Equal("unsafe_entry", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(destination, "good.pdb")));
    }

    [Fact]
    public void Extract_RejectsDuplicateTargetsUnlessOverwrite()
    {
        string zip = Path.Combine(VestigoPaths.TraceDirectory, "dup.zip");
        CreateZip(zip, ("dup.pdb", "one"), ("dup.pdb", "two"));
        TraceArchiveExtractor extractor = CreateExtractor();

        Assert.Equal("already_exists", extractor.Extract(zip, VestigoPaths.TraceDirectory, overwrite: false).ErrorCode);
        Assert.False(File.Exists(Path.Combine(VestigoPaths.TraceDirectory, "dup.pdb")));

        Assert.True(extractor.Extract(zip, VestigoPaths.TraceDirectory, overwrite: true).Success);
        Assert.Equal("two", File.ReadAllText(Path.Combine(VestigoPaths.TraceDirectory, "dup.pdb")));
    }

    [Fact]
    public void Extract_HonorsOverwriteAndEntryCap()
    {
        string zip = Path.Combine(VestigoPaths.TraceDirectory, "once.zip");
        CreateZip(zip, ("keep.pdb", "first"));
        TraceArchiveExtractor extractor = CreateExtractor();
        Assert.True(extractor.Extract(zip, VestigoPaths.TraceDirectory, overwrite: false).Success);
        File.WriteAllText(Path.Combine(VestigoPaths.TraceDirectory, "keep.pdb"), "first");
        Assert.Equal("already_exists", extractor.Extract(zip, VestigoPaths.TraceDirectory, overwrite: false).ErrorCode);

        VestigoOptions tiny = new() { MaxArchiveEntries = 1 };
        string many = Path.Combine(VestigoPaths.TraceDirectory, "many.zip");
        CreateZip(many, ("a.pdb", "a"), ("b.pdb", "b"));
        Assert.Equal("archive_too_large", new TraceArchiveExtractor(tiny, new PathPolicy(tiny)).Extract(
            many,
            VestigoPaths.TraceDirectory,
            overwrite: true).ErrorCode);
    }

    [Fact]
    public void IsUnsafeEntry_CoversTheGuards()
    {
        Assert.True(TraceArchiveExtractor.IsUnsafeEntry(@"..\x.pdb"));
        Assert.True(TraceArchiveExtractor.IsUnsafeEntry("/abs.pdb"));
        Assert.True(TraceArchiveExtractor.IsUnsafeEntry(@"D:\abs.pdb"));
        Assert.True(TraceArchiveExtractor.IsUnsafeEntry("file:ads.pdb"));
        Assert.False(TraceArchiveExtractor.IsUnsafeEntry(@"nested\driver.pdb"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
    }

    private static TraceArchiveExtractor CreateExtractor() =>
        new(new VestigoOptions(), new PathPolicy(new VestigoOptions()));

    private OperationResult<TraceArchiveExtraction> ExtractUnsafe(string entryName)
    {
        string zip = Path.Combine(VestigoPaths.TraceDirectory, Guid.NewGuid().ToString("N") + ".zip");
        CreateZip(zip, (entryName, "x"));
        return CreateExtractor().Extract(zip, VestigoPaths.TraceDirectory, overwrite: true);
    }

    private static void CreateZip(string path, params (string Name, string Body)[] entries)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive zip = new(stream, ZipArchiveMode.Create);
        foreach ((string name, string body) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write(body);
        }
    }
}
