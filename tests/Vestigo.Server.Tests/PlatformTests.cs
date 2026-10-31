using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class PlatformTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-platform-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VestigoOptions Options(RootAccess access = RootAccess.Read | RootAccess.Write) => new()
    {
        Roots = [new RootDefinition { Name = "artifacts", Path = _root, Access = access }]
    };

    [Fact]
    public void RootPolicy_ResolvesNamedRoot()
    {
        PathCheck check = new RootPolicy(Options()).Resolve(@"artifacts:bin\a.sys", RootAccess.Write);

        Assert.True(check.Success);
        Assert.Equal(Path.Combine(_root, "bin", "a.sys"), check.FullPath);
    }

    [Fact]
    public void RootPolicy_RejectsTraversalOutOfNamedRoot()
    {
        PathCheck check = new RootPolicy(Options()).Resolve(@"artifacts:..\outside.txt", RootAccess.Read);

        Assert.False(check.Success);
        Assert.Equal("outside_root", check.ErrorCode);
    }

    [Fact]
    public void RootPolicy_EnforcesAccessFlags()
    {
        RootPolicy policy = new(Options(RootAccess.Read));

        Assert.Equal("access_denied", policy.Resolve(@"artifacts:a.txt", RootAccess.Execute).ErrorCode);
        Assert.Equal("access_denied", policy.Resolve(Path.Combine(_root, "a.txt"), RootAccess.Write).ErrorCode);
        Assert.True(policy.Resolve(Path.Combine(_root, "a.txt"), RootAccess.Read).Success);
    }

    [Fact]
    public void RootPolicy_ReportsUnknownRootAndMissingConfiguration()
    {
        Assert.Equal("unknown_root", new RootPolicy(Options()).Resolve("other:a.txt", RootAccess.Read).ErrorCode);
        Assert.Equal("roots_not_configured", new RootPolicy(new VestigoOptions()).Resolve("artifacts:a", RootAccess.Read).ErrorCode);
    }

    [Fact]
    public void Options_RejectBadRoots()
    {
        VestigoOptions duplicate = Options();
        duplicate.Roots.Add(new RootDefinition { Name = "ARTIFACTS", Path = _root, Access = RootAccess.Read });
        VestigoOptions relative = new() { Roots = [new RootDefinition { Name = "rel", Path = "relative", Access = RootAccess.Read }] };
        VestigoOptions shortName = new() { Roots = [new RootDefinition { Name = "c", Path = _root, Access = RootAccess.Read }] };

        Assert.Throws<InvalidOperationException>(duplicate.Validate);
        Assert.Throws<InvalidOperationException>(relative.Validate);
        Assert.Throws<InvalidOperationException>(shortName.Validate);
    }

    [Fact]
    public async Task JobManager_CapturesOutputAndExitCode()
    {
        JobManager jobs = new(new VestigoOptions(), Path.Combine(_root, "jobs"));
        OperationResult<JobInfo> started = jobs.Start("test", "echo", (context, _) =>
        {
            context.WriteLine("héllo");
            return Task.FromResult(3);
        });

        OperationResult<JobInfo> done = await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(10), default);
        OperationResult<JobOutputPage> page = jobs.ReadOutput(started.Value.Id, 0, null);

        Assert.Equal(JobState.Failed, done.Value!.State);
        Assert.Equal(3, done.Value.ExitCode);
        Assert.Contains("héllo", page.Value!.Text, StringComparison.Ordinal);
        Assert.True(page.Value.Completed);
    }

    [Fact]
    public async Task JobManager_CancelsRunningJob()
    {
        JobManager jobs = new(new VestigoOptions(), Path.Combine(_root, "jobs"));
        OperationResult<JobInfo> started = jobs.Start("test", "wait", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        });

        jobs.Cancel(started.Value!.Id);
        OperationResult<JobInfo> done = await jobs.WaitAsync(started.Value.Id, TimeSpan.FromSeconds(10), default);

        Assert.Equal(JobState.Cancelled, done.Value!.State);
    }

    [Fact]
    public async Task JobManager_EnforcesConcurrencyAndOutputLimits()
    {
        JobManager jobs = new(new VestigoOptions { MaxConcurrentJobs = 1, MaxJobOutputBytes = 32 }, Path.Combine(_root, "jobs"));
        TaskCompletionSource gate = new();
        OperationResult<JobInfo> first = jobs.Start("test", "hold", async (context, _) =>
        {
            context.Write(new string('x', 100));
            await gate.Task;
            return 0;
        });

        OperationResult<JobInfo> second = jobs.Start("test", "extra", (_, _) => Task.FromResult(0));
        gate.SetResult();
        await jobs.WaitAsync(first.Value!.Id, TimeSpan.FromSeconds(10), default);

        Assert.Equal("too_many_jobs", second.ErrorCode);
        Assert.Contains("truncated", jobs.ReadOutput(first.Value.Id, 0, null).Value!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void JobManager_Startup_DeletesOnlyStaleOrphanLogs()
    {
        string dir = Path.Combine(_root, "jobs-stale");
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, "old.log");
        string fresh = Path.Combine(dir, "new.log");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        _ = new JobManager(new VestigoOptions { JobRetention = TimeSpan.FromHours(1) }, dir);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task JobManager_PurgesOldJobs()
    {
        JobManager jobs = new(new VestigoOptions { JobRetention = TimeSpan.FromMinutes(1) }, Path.Combine(_root, "jobs"));
        OperationResult<JobInfo> started = jobs.Start("test", "quick", (_, _) => Task.FromResult(0));
        await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(10), default);

        Assert.Equal(1, jobs.Purge(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("job_not_found", jobs.Get(started.Value.Id).ErrorCode);
    }

    [Fact]
    public async Task JobManager_ParallelStartsNeverExceedConcurrencyLimit()
    {
        JobManager jobs = new(new VestigoOptions { MaxConcurrentJobs = 2 }, Path.Combine(_root, "jobs"));
        TaskCompletionSource gate = new();

        OperationResult<JobInfo>[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            jobs.Start("test", "hold", async (_, _) =>
            {
                await gate.Task;
                return 0;
            }))));
        gate.SetResult();

        Assert.Equal(2, results.Count(result => result.Success));
        foreach (OperationResult<JobInfo> ok in results.Where(result => result.Success))
        {
            await jobs.WaitAsync(ok.Value!.Id, TimeSpan.FromSeconds(10), default);
        }
    }

    [Fact]
    public async Task JobManager_ReadOutput_AdvancesPastTinyWindowAndFlushesPartialTail()
    {
        JobManager jobs = new(new VestigoOptions(), Path.Combine(_root, "jobs"));
        OperationResult<JobInfo> started = jobs.Start("test", "utf8", (context, _) =>
        {
            context.Write("é");
            return Task.FromResult(0);
        });
        await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(10), default);

        JobOutputPage page = jobs.ReadOutput(started.Value.Id, 0, 1).Value!;

        Assert.True(page.NextOffset > 0);
    }

    [Fact]
    public void Utf8SafeLength_DoesNotSplitCharacters()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("aé");

        Assert.Equal(1, JobManager.Utf8SafeLength(bytes, 2, endOfData: false));
        Assert.Equal(3, JobManager.Utf8SafeLength(bytes, 3, endOfData: false));
    }

    [Fact]
    public void Modules_HonourConfigurationAndRejectUnknownNames()
    {
        VestigoOptions off = new() { Modules = { ["Trace"] = new ModuleSettings { Enabled = false } } };
        VestigoOptions unknown = new() { Modules = { ["Nope"] = new ModuleSettings() } };

        Assert.DoesNotContain(ModuleRegistry.Enabled(off), module => module.Name == "Trace");
        Assert.Contains(ModuleRegistry.Enabled(new VestigoOptions()), module => module.Name == "Trace");
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(unknown));
        Assert.All(ModuleRegistry.All, module => Assert.NotEmpty(module.ToolTypes));
    }

    [Fact]
    public void ValidateConfiguration_RejectsModuleWhoseDependencyIsDisabled()
    {
        VestigoOptions options = new()
        {
            ApiTokens = ["t"],
            Modules =
            {
                ["Jobs"] = new ModuleSettings { Enabled = false },
                ["Processes"] = new ModuleSettings { Enabled = true },
                ["RemoteDebugger"] = new ModuleSettings { Enabled = true }
            }
        };

        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options));
        ModuleRegistry.ValidateConfiguration(new VestigoOptions());
    }

    [Fact]
    public void ProcessesModule_IsOptInAndRequiresJobsAndTokens()
    {
        ModuleSettings on = new() { Enabled = true };
        VestigoOptions defaults = new();
        VestigoOptions noTokens = new() { Modules = { ["Processes"] = on } };
        VestigoOptions noJobs = new()
        {
            ApiTokens = ["t"],
            Modules = { ["Processes"] = on, ["Jobs"] = new ModuleSettings { Enabled = false } }
        };
        VestigoOptions valid = new() { ApiTokens = ["t"], Modules = { ["Processes"] = on } };

        Assert.DoesNotContain(ModuleRegistry.Enabled(defaults), module => module.Name == "Processes");
        ModuleRegistry.ValidateConfiguration(defaults);
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(noTokens));
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(noJobs));
        ModuleRegistry.ValidateConfiguration(valid);
    }

    [Fact]
    public void RemoteDebuggerModule_IsOptInAndRequiresJobsAndProcesses()
    {
        ModuleSettings on = new() { Enabled = true };
        VestigoOptions defaults = new();
        VestigoOptions noProcesses = new() { ApiTokens = ["t"], Modules = { ["RemoteDebugger"] = on } };
        VestigoOptions valid = new()
        {
            ApiTokens = ["t"],
            Modules = { ["Processes"] = on, ["RemoteDebugger"] = on }
        };

        Assert.DoesNotContain(ModuleRegistry.Enabled(defaults), module => module.Name == "RemoteDebugger");
        ModuleRegistry.ValidateConfiguration(defaults);
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(noProcesses));
        ModuleRegistry.ValidateConfiguration(valid);
    }
}
