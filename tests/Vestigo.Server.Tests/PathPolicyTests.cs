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
}
