using Vestigo.Jobs;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class ServiceProcessRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-svcrun-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JobContext NewContext(long limit = 1024 * 1024)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllBytes(path, []);
        return new JobContext("test", path, limit);
    }

    private static LaunchRequest Request(params string[] arguments) =>
        new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments, null, new Dictionary<string, string>(), LaunchContext.Service, false);

    [Fact]
    public async Task Run_CapturesStdoutAndEnvironmentAndExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        JobContext output = NewContext();
        LaunchRequest request = Request("/c", "echo %VESTIGO_X%& exit 7") with
        {
            Environment = new Dictionary<string, string> { ["VESTIGO_X"] = "hello world" }
        };

        int exit = await new ProcessLauncher().RunAsync(request, output, TestContext.Current.CancellationToken);

        Assert.Equal(7, exit);
        Assert.Contains("hello world", File.ReadAllText(output.OutputPath));
    }

    [Fact]
    public async Task Run_CapturesStderr()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        JobContext output = NewContext();

        int exit = await new ProcessLauncher().RunAsync(Request("/c", "echo oops 1>&2"), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("oops", File.ReadAllText(output.OutputPath));
    }

    [Fact]
    public async Task Run_UsesWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        JobContext output = NewContext();
        string directory = Directory.CreateDirectory(Path.Combine(_root, "wd")).FullName;

        await new ProcessLauncher().RunAsync(Request("/c", "cd") with { WorkingDirectory = directory }, output, TestContext.Current.CancellationToken);

        Assert.Contains(directory, File.ReadAllText(output.OutputPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_MissingExecutable_ThrowsStartFailed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        LaunchRequest request = Request() with { Executable = Path.Combine(_root, "missing.exe") };

        ProcessLaunchException ex = await Assert.ThrowsAsync<ProcessLaunchException>(
            () => new ProcessLauncher().RunAsync(request, NewContext(), TestContext.Current.CancellationToken));

        Assert.Equal("start_failed", ex.Code);
    }

    [Fact]
    public async Task Run_Cancel_KillsProcessTreeAndThrows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new();
        Task<int> run = new ProcessLauncher().RunAsync(Request("/c", "ping -n 60 127.0.0.1 >nul"), NewContext(), cts.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Run_OutputCap_WritesTruncationMarkerAndDropsRest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        JobContext output = NewContext(limit: 64);

        int exit = await new ProcessLauncher().RunAsync(
            Request("/c", "for /l %i in (1,1,200) do @echo line number %i padding padding"),
            output,
            TestContext.Current.CancellationToken);

        string text = File.ReadAllText(output.OutputPath);
        Assert.Equal(0, exit);
        Assert.Contains("[output truncated by MaxJobOutputBytes]", text);
        Assert.True(output.OutputBytes < 64 + 100);
        Assert.DoesNotContain("line number 200", text);
    }
}