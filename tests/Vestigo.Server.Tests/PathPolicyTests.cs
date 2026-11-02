using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class PathPolicyTests
{
    [Fact]
    public void RequireExistingFile_AcceptsFileInsideRoot()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-in").FullName;
        string file = Path.Combine(root, "trace.etl");
        File.WriteAllText(file, "etl");
        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [root], MaxInputBytes = 100 });

        PathCheck result = policy.RequireExistingFile(file, [".etl"]);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(file), result.FullPath);
    }

    [Fact]
    public void RequireExistingFile_RejectsSiblingPrefix()
    {
        string parent = Directory.CreateTempSubdirectory("vestigo-parent").FullName;
        string root = Directory.CreateDirectory(Path.Combine(parent, "allowed")).FullName;
        Directory.CreateDirectory(Path.Combine(parent, "allowed-extra"));
        string outside = Path.Combine(parent, "allowed-extra", "trace.etl");
        File.WriteAllText(outside, "etl");
        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [root] });

        PathCheck result = policy.RequireExistingFile(outside, [".etl"]);

        Assert.False(result.Success);
        Assert.Equal("outside_root", result.ErrorCode);
    }

    [Fact]
    public void RequireWritableDirectory_AcceptsOutputRootWhenInputIsDisjoint()
    {
        string input = Directory.CreateTempSubdirectory("vestigo-in").FullName;
        string output = Directory.CreateTempSubdirectory("vestigo-out").FullName;
        PathPolicy policy = new(new VestigoOptions
        {
            AllowedInputRoots = [input],
            AllowedOutputRoots = [output]
        });
        string destination = Path.Combine(output, "extract");
        string fromInput = Path.Combine(input, "extract");

        PathCheck dest = policy.RequireWritableDirectory(destination);
        PathCheck rejected = policy.RequireWritableDirectory(fromInput);

        Assert.True(dest.Success, dest.Error);
        Assert.True(Directory.Exists(dest.FullPath));
        Assert.Equal(Path.GetFullPath(destination), dest.FullPath);
        Assert.Equal("outside_root", rejected.ErrorCode);
        Assert.False(Directory.Exists(fromInput));
    }

    [Fact]
    public void RequireOutputFile_RejectsOverwriteByDefault()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-out").FullName;
        string output = Path.Combine(root, "events.ndjson");
        File.WriteAllText(output, "existing");
        PathPolicy policy = new(new VestigoOptions { AllowedOutputRoots = [root] });

        PathCheck result = policy.RequireOutputFile(output, [".ndjson"], overwrite: false);

        Assert.Equal("already_exists", result.ErrorCode);
    }

    [Fact]
    public void RequireExistingFile_RejectsOversizedInput()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-size").FullName;
        string file = Path.Combine(root, "trace.etl");
        File.WriteAllBytes(file, [1, 2, 3]);
        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [root], MaxInputBytes = 2 });

        PathCheck result = policy.RequireExistingFile(file, [".etl"]);

        Assert.Equal("file_too_large", result.ErrorCode);
    }

    [Fact]
    public void RequireExistingFile_RejectsSymlinkThatEscapesRoot()
    {
        string parent = Directory.CreateTempSubdirectory("vestigo-link").FullName;
        string root = Directory.CreateDirectory(Path.Combine(parent, "allowed")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(parent, "secret")).FullName;
        string secret = Path.Combine(outside, "trace.etl");
        File.WriteAllText(secret, "etl");
        string link = Path.Combine(root, "escape.etl");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic link privilege is not available on this machine.");
        }

        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [root] });

        PathCheck result = policy.RequireExistingFile(link, [".etl"]);

        Assert.Equal("outside_root", result.ErrorCode);

        string directoryLink = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(directoryLink, outside);
        PathCheck throughDirectory = policy.RequireExistingFile(Path.Combine(directoryLink, "trace.etl"), [".etl"]);
        Assert.Equal("outside_root", throughDirectory.ErrorCode);
    }

    [Fact]
    public void RequireExistingFile_AcceptsSymlinkThatStaysInsideRoot()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-inside-link").FullName;
        string file = Path.Combine(root, "trace.etl");
        File.WriteAllText(file, "etl");
        string link = Path.Combine(root, "alias.etl");
        try
        {
            File.CreateSymbolicLink(link, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic link privilege is not available on this machine.");
        }

        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [root], MaxInputBytes = 100 });

        PathCheck result = policy.RequireExistingFile(link, [".etl"]);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(file), result.FullPath);
    }
}
