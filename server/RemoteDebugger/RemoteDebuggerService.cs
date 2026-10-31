using System.Collections.Concurrent;
using System.Diagnostics;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Processes;

namespace Vestigo.RemoteDebugger;

public sealed record RemoteDebuggerInstallation(
    string Version,
    string Arch,
    bool Installed,
    string? Path,
    string? Source,
    string? FileVersion,
    string InstallerUrl);

public sealed record RemoteDebuggerProcessInfo(
    int Pid,
    int SessionId,
    string? Path,
    string? Version,
    string? Arch,
    DateTimeOffset? StartTime,
    string? CommandLine,
    int Port,
    string PortSource,
    bool Listening,
    bool StartedByVestigo);

public sealed record RemoteDebuggerStatus(
    IReadOnlyList<RemoteDebuggerInstallation> Installations,
    IReadOnlyList<RemoteDebuggerProcessInfo> Processes);

public sealed record InstallRemoteDebuggerResult(bool Skipped, string? ExistingPath, JobInfo? Job, string Url);

public sealed record StartRemoteDebuggerResult(
    int? Pid,
    int Port,
    string Executable,
    string Arguments,
    bool Listening,
    string JobId,
    string Auth,
    IReadOnlyList<string> Warnings);

public sealed record StopRemoteDebuggerOutcome(int Pid, bool Graceful, bool Killed, string? Error);

public sealed record PrepareRemoteDebuggerResult(string Executable, string Arguments, JobInfo Job);

/// <summary>Orchestrates msvsmon discovery, installation, start, and stop. Tools stay thin wrappers.</summary>
public sealed class RemoteDebuggerService(
    VestigoOptions options,
    JobManager jobs,
    IProcessLauncher launcher,
    AppCloser closer,
    MsvsmonLocator locator,
    IAuthenticodeVerifier verifier,
    IHttpClientFactory httpClientFactory,
    ILogger<RemoteDebuggerService>? logger = null)
{
    public const string HttpClientName = "remote-debugger";
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan StartProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<int, StartedInstance> _started = new();
    private readonly ConcurrentDictionary<int, string> _pendingPorts = new();
    private readonly object _startGate = new();

    private sealed record StartedInstance(int Pid, int Port, string JobId);

    public RemoteDebuggerStatus GetStatus(DebuggerVersion? version, DebuggerArch? arch)
    {
        List<RemoteDebuggerInstallation> installations = [];
        foreach (DebuggerVersion candidateVersion in new[] { DebuggerVersion.Vs2022, DebuggerVersion.Vs2026 })
        {
            foreach (DebuggerArch candidateArch in new[] { DebuggerArch.X64, DebuggerArch.Arm64, DebuggerArch.X86 })
            {
                if ((version is not null && version != candidateVersion) || (arch is not null && arch != candidateArch))
                {
                    continue;
                }

                MsvsmonInstall? found = locator.Find(candidateVersion, candidateArch);
                installations.Add(new RemoteDebuggerInstallation(
                    RemoteDebuggerPlanner.VersionLabel(candidateVersion),
                    RemoteDebuggerPlanner.ArchFolder(candidateArch),
                    found is not null,
                    found?.Path,
                    found?.Source,
                    found is null ? null : FileVersion(found.Path),
                    RemoteDebuggerPlanner.InstallerUrl(candidateVersion, candidateArch).ToString()));
            }
        }

        List<RemoteDebuggerProcessInfo> processes = [];
        foreach (MsvsmonProcess process in MsvsmonProcesses.List(readCommandLines: true))
        {
            RemoteDebuggerInstallation? match = installations.FirstOrDefault(item =>
                item.Path is not null && string.Equals(item.Path, process.Path, StringComparison.OrdinalIgnoreCase));
            if ((version is not null || arch is not null) && match is null)
            {
                continue;
            }

            bool tracked = _started.TryGetValue(process.Pid, out StartedInstance? instance);
            int port = process.Port ?? (tracked ? instance!.Port : options.RemoteDebugger.DefaultPort);
            string source = process.Port is not null ? "commandLine" : tracked ? "vestigo" : "assumedDefault";
            processes.Add(new RemoteDebuggerProcessInfo(
                process.Pid,
                process.SessionId,
                process.Path,
                match?.Version,
                match?.Arch,
                process.StartTime,
                process.CommandLine,
                port,
                source,
                MsvsmonProcesses.IsListening(port),
                tracked));
        }

        return new RemoteDebuggerStatus(installations, processes);
    }

    public OperationResult<InstallRemoteDebuggerResult> Install(DebuggerVersion version, DebuggerArch arch, bool force)
    {
        Uri url = RemoteDebuggerPlanner.InstallerUrl(version, arch);
        MsvsmonInstall? existing = locator.Find(version, arch);
        if (existing is not null && !force)
        {
            return OperationResult<InstallRemoteDebuggerResult>.Ok(new InstallRemoteDebuggerResult(true, existing.Path, null, url.ToString()));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Service, false);
        if (!available.Success)
        {
            return Fail<InstallRemoteDebuggerResult>(available.ErrorCode!, available.Error!);
        }

        OperationResult<JobInfo> started = jobs.Start(
            "remote-debugger-install",
            $"install Remote Debugger {RemoteDebuggerPlanner.VersionLabel(version)} {RemoteDebuggerPlanner.ArchFolder(arch)}",
            (output, token) => DownloadVerifyInstallAsync(url, output, token));
        return started.Success
            ? OperationResult<InstallRemoteDebuggerResult>.Ok(new InstallRemoteDebuggerResult(false, existing?.Path, started.Value, url.ToString()))
            : Fail<InstallRemoteDebuggerResult>(started.ErrorCode!, started.Error!);
    }

    private async Task<int> DownloadVerifyInstallAsync(Uri url, JobContext output, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(VestigoPaths.SpoolDirectory, "remote-debugger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "VS_RemoteTools.exe");
        try
        {
            output.WriteLine($"Downloading {url}");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RemoteDebugger.InstallerDownloadTimeout);
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Installer download did not end on an HTTPS URL.");
            }

            if (response.Content.Headers.ContentLength > MaxInstallerBytes)
            {
                throw new InvalidOperationException("Installer is larger than the 512 MiB limit.");
            }

            await using (FileStream target = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes)
                    {
                        throw new InvalidOperationException("Installer exceeded the 512 MiB limit.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            output.WriteLine($"Downloaded {new FileInfo(file).Length} bytes; verifying Authenticode signature.");
            OperationResult<string> signature = verifier.Verify(file);
            if (!signature.Success)
            {
                output.WriteLine("Refusing to run installer: " + signature.Error);
                return 1;
            }

            if (!RemoteDebuggerPlanner.IsMicrosoftSigner(signature.Value))
            {
                output.WriteLine($"Refusing to run installer: signer '{signature.Value}' is not Microsoft Corporation.");
                return 1;
            }

            output.WriteLine($"Signature valid, signer: {signature.Value}");
            LaunchRequest request = new(file, RemoteDebuggerPlanner.InstallArguments, directory, new Dictionary<string, string>(), LaunchContext.Service, false);
            output.WriteLine("Running: " + RemoteDebuggerPlanner.Describe(RemoteDebuggerPlanner.InstallArguments));
            int exitCode = await launcher.RunAsync(request, output, cancellationToken).ConfigureAwait(false);
            if (exitCode == 3010)
            {
                output.WriteLine("Installation succeeded; a restart is required to finish.");
                return 0;
            }

            return exitCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            output.WriteLine("Install failed: " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Could not delete installer directory {Directory}: {Message}", directory, ex.Message);
            }
        }
    }

    public async Task<OperationResult<StartRemoteDebuggerResult>> StartAsync(
        DebuggerVersion version,
        DebuggerArch arch,
        int? requestedPort,
        int timeoutSeconds,
        DebuggerAuth auth,
        bool confirmNoAuth,
        IReadOnlyList<string> allowUsers,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken)
    {
        int port = requestedPort ?? options.RemoteDebugger.DefaultPort;
        if (RemoteDebuggerPlanner.ValidateStart(port, timeoutSeconds, auth, confirmNoAuth, allowUsers) is { } invalid)
        {
            return Fail<StartRemoteDebuggerResult>("invalid_request", invalid);
        }

        MsvsmonInstall? install = locator.Find(version, arch);
        if (install is null)
        {
            return Fail<StartRemoteDebuggerResult>("not_found", "msvsmon.exe was not found for that version and architecture. Run install_remote_debugger first.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(context, elevated);
        if (!available.Success)
        {
            return Fail<StartRemoteDebuggerResult>(available.ErrorCode!, available.Error!);
        }

        IReadOnlyList<string> args = RemoteDebuggerPlanner.BuildStartArguments(port, timeoutSeconds, auth, allowUsers);
        List<string> warnings = [];
        if (auth == DebuggerAuth.None)
        {
            warnings.Add(RemoteDebuggerPlanner.NoAuthWarning);
            logger?.LogWarning("Starting msvsmon on port {Port} with authentication disabled", port);
        }

        HashSet<int> before;
        OperationResult<JobInfo> job;
        lock (_startGate)
        {
            if (PortConflict(port) is { } conflict)
            {
                return Fail<StartRemoteDebuggerResult>("port_in_use", conflict);
            }

            before = MsvsmonProcesses.List(readCommandLines: false).Select(item => item.Pid).ToHashSet();
            LaunchRequest request = new(install.Path, args, Path.GetDirectoryName(install.Path), new Dictionary<string, string>(), context, elevated);
            job = jobs.Start("remote-debugger", $"msvsmon {RemoteDebuggerPlanner.VersionLabel(version)} port {port}", (output, token) =>
            {
                output.WriteLine("Starting: " + install.Path + " " + RemoteDebuggerPlanner.Describe(args));
                return launcher.RunAsync(request, output, token);
            });
            if (!job.Success)
            {
                return Fail<StartRemoteDebuggerResult>(job.ErrorCode!, job.Error!);
            }

            _pendingPorts[port] = job.Value!.Id;
        }

        string jobId = job.Value!.Id;
        int? pid = null;
        bool listening = false;
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < StartProbeTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (jobs.Get(jobId).Value is { State: not JobState.Running } ended)
                {
                    string tail = jobs.ReadOutput(jobId, 0, 4096).Value?.Text ?? string.Empty;
                    return Fail<StartRemoteDebuggerResult>(
                        "start_failed",
                        $"msvsmon exited immediately (job {jobId}, exit code {ended.ExitCode}). {ended.Error} {tail}".Trim());
                }

                MsvsmonProcess? created = MsvsmonProcesses.List(readCommandLines: false)
                    .FirstOrDefault(item => !before.Contains(item.Pid)
                                            && string.Equals(item.Path, install.Path, StringComparison.OrdinalIgnoreCase));
                pid ??= created?.Pid;
                listening = MsvsmonProcesses.IsListening(port);
                if (pid is not null && listening)
                {
                    break;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (pid is not { } started)
            {
                jobs.Cancel(jobId);
                return Fail<StartRemoteDebuggerResult>("start_failed", $"The msvsmon process id could not be determined within the startup window; the start job {jobId} was cancelled.");
            }

            _started[started] = new StartedInstance(started, port, jobId);
        }
        catch (OperationCanceledException)
        {
            jobs.Cancel(jobId);
            throw;
        }
        finally
        {
            _pendingPorts.TryRemove(port, out _);
        }

        return OperationResult<StartRemoteDebuggerResult>.Ok(new StartRemoteDebuggerResult(
            pid, port, install.Path, RemoteDebuggerPlanner.Describe(args), listening, jobId, auth.ToString().ToLowerInvariant(), warnings));
    }

    /// <summary>Returns a refusal message when another monitor owns or listens on <paramref name="port" />.</summary>
    private string? PortConflict(int port)
    {
        if (_pendingPorts.ContainsKey(port))
        {
            return $"A monitor is already starting on port {port}.";
        }

        PruneStarted();
        if (_started.Values.Any(item => item.Port == port))
        {
            return $"A monitor started by Vestigo already uses port {port}. Stop it first or pick another port.";
        }

        foreach (MsvsmonProcess process in MsvsmonProcesses.List(readCommandLines: true))
        {
            if ((process.Port ?? options.RemoteDebugger.DefaultPort) == port)
            {
                return $"msvsmon (pid {process.Pid}) is already running on port {port}.";
            }
        }

        return MsvsmonProcesses.IsListening(port) ? $"Port {port} is already in use by another listener." : null;
    }

    private void PruneStarted()
    {
        HashSet<int> alive = MsvsmonProcesses.List(readCommandLines: false).Select(item => item.Pid).ToHashSet();
        foreach (int pid in _started.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _started.TryRemove(pid, out _);
        }
    }

    public async Task<OperationResult<IReadOnlyList<StopRemoteDebuggerOutcome>>> StopAsync(
        int? pid,
        bool all,
        int gracefulTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (pid is null == !all)
        {
            return Fail<IReadOnlyList<StopRemoteDebuggerOutcome>>("invalid_request", "Provide exactly one of pid or all: true.");
        }

        PruneStarted();
        List<StartedInstance> targets;
        if (pid is { } id)
        {
            if (ProcessGuards.CheckKillTarget(id, Environment.ProcessId) is { } refusal)
            {
                return Fail<IReadOnlyList<StopRemoteDebuggerOutcome>>("refused", refusal);
            }

            if (MsvsmonProcesses.List(readCommandLines: false).All(item => item.Pid != id))
            {
                return Fail<IReadOnlyList<StopRemoteDebuggerOutcome>>("not_found", $"Process {id} is not a running msvsmon.exe.");
            }

            targets = [_started.TryGetValue(id, out StartedInstance? known) ? known : new StartedInstance(id, 0, string.Empty)];
        }
        else
        {
            targets = _started.Values.ToList();
            if (targets.Count == 0)
            {
                return Fail<IReadOnlyList<StopRemoteDebuggerOutcome>>("not_found", "No msvsmon instance started by Vestigo is running.");
            }
        }

        int timeout = Math.Clamp(gracefulTimeoutSeconds, 0, 120);
        List<StopRemoteDebuggerOutcome> outcomes = [];
        foreach (StartedInstance target in targets)
        {
            outcomes.Add(await StopOneAsync(target, timeout, cancellationToken).ConfigureAwait(false));
            _started.TryRemove(target.Pid, out _);
        }

        return OperationResult<IReadOnlyList<StopRemoteDebuggerOutcome>>.Ok(outcomes);
    }

    private async Task<StopRemoteDebuggerOutcome> StopOneAsync(StartedInstance target, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = Process.GetProcessById(target.Pid);
            if (timeoutSeconds > 0)
            {
                try
                {
                    await closer.CloseAsync(new CloseSelector([target.Pid], [], null), 0, LaunchContext.Interactive, false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug("Graceful close of msvsmon {Pid} failed: {Message}", target.Pid, ex.Message);
                }

                using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    return new StopRemoteDebuggerOutcome(target.Pid, true, false, null);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Fall through to the kill.
                }
            }
        }
        catch (ArgumentException)
        {
            return new StopRemoteDebuggerOutcome(target.Pid, true, false, null);
        }

        if (target.JobId.Length > 0)
        {
            jobs.Cancel(target.JobId);
        }

        OperationResult<IReadOnlyList<KillOutcome>> killed = ProcessControl.Kill(target.Pid, null, true, Environment.ProcessId);
        if (killed.ErrorCode == "not_found")
        {
            return new StopRemoteDebuggerOutcome(target.Pid, true, false, null);
        }

        KillOutcome? outcome = killed.Value?.FirstOrDefault();
        return new StopRemoteDebuggerOutcome(target.Pid, false, outcome?.Killed ?? false, outcome?.Error ?? killed.Error);
    }

    public OperationResult<PrepareRemoteDebuggerResult> Prepare(DebuggerVersion version, DebuggerArch arch, LaunchContext context)
    {
        MsvsmonInstall? install = locator.Find(version, arch);
        if (install is null)
        {
            return Fail<PrepareRemoteDebuggerResult>("not_found", "msvsmon.exe was not found for that version and architecture. Run install_remote_debugger first.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(context, false);
        if (!available.Success)
        {
            return Fail<PrepareRemoteDebuggerResult>(available.ErrorCode!, available.Error!);
        }

        LaunchRequest request = new(
            install.Path, RemoteDebuggerPlanner.PrepareArguments, Path.GetDirectoryName(install.Path), new Dictionary<string, string>(), context, false);
        OperationResult<JobInfo> job = jobs.Start(
            "remote-debugger-prepare",
            $"msvsmon /prepcomputer {RemoteDebuggerPlanner.VersionLabel(version)}",
            (output, token) => launcher.RunAsync(request, output, token));
        return job.Success
            ? OperationResult<PrepareRemoteDebuggerResult>.Ok(new PrepareRemoteDebuggerResult(
                install.Path, RemoteDebuggerPlanner.Describe(RemoteDebuggerPlanner.PrepareArguments), job.Value!))
            : Fail<PrepareRemoteDebuggerResult>(job.ErrorCode!, job.Error!);
    }

    private static string? FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private static OperationResult<T> Fail<T>(string code, string message) => OperationResult<T>.Fail(code, message);
}
