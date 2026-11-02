using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Security;
using Vestigo.Software;

namespace Vestigo.Updates;

/// <summary>Locates the packaged updater, launches a silent self-update, and remembers the last attempt.</summary>
public interface ISelfUpdateRunner
{
    bool IsInstalled { get; }

    string? TrustFailure { get; }

    bool IsInProgress { get; }

    SelfUpdateAttempt? ReadLastAttempt(string? installedVersion);

    Task<OperationResult<SelfUpdateResult>> StartAsync(
        string fromVersion,
        string targetVersion,
        bool dryRun,
        CancellationToken cancellationToken);
}

/// <summary>
///     Starts <c>Nefarius_Vestigo_Updater.exe --silent-update</c> without a kill-on-close job object so the
///     service can stop while the temporary Vicius copy finishes the MSI.
/// </summary>
public sealed class SelfUpdateRunner : ISelfUpdateRunner
{
    internal const int TailBytes = 16 * 1024;
    internal const int LaunchTimeoutSeconds = 120;
    internal const int UpToDateExitCode = 202;
    internal const int InstalledExitCode = 203;
    internal const int HandedOffExitCode = 207;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private int _running;

    private readonly string _installDirectory;
    private readonly string _recordDirectory;
    private readonly Func<string, string?> _trust;
    private readonly Func<string, IReadOnlyList<string>, string, int> _launch;
    private readonly Func<bool> _updaterRunning;

    public SelfUpdateRunner()
        : this(
            AppContext.BaseDirectory,
            Path.Combine(VestigoPaths.SettingsDirectory, "updates"))
    {
    }

    internal SelfUpdateRunner(
        string installDirectory,
        string recordDirectory,
        Func<string, string?>? trust = null,
        Func<string, IReadOnlyList<string>, string, int>? launch = null,
        Func<bool>? updaterRunning = null)
    {
        _installDirectory = installDirectory;
        _recordDirectory = recordDirectory;
        _trust = trust ?? ExecutableTrust.Check;
        _launch = launch ?? LaunchUpdater;
        _updaterRunning = updaterRunning ?? UpdaterProcessIsRunning;
    }

    public bool IsInstalled => File.Exists(UpdaterPath) && File.Exists(ConfigurationPath);

    public string? TrustFailure => IsInstalled ? _trust(UpdaterPath) : "the updater executable is not installed";

    public bool IsInProgress => Volatile.Read(ref _running) != 0 || _updaterRunning();

    private string UpdaterPath => Path.Combine(_installDirectory, UpdateEndpoints.UpdaterExecutable);

    private string ConfigurationPath => Path.Combine(_installDirectory, UpdateEndpoints.UpdaterConfiguration);

    private string RecordPath => Path.Combine(_recordDirectory, "last-self-update.json");

    public SelfUpdateAttempt? ReadLastAttempt(string? installedVersion)
    {
        SelfUpdateRecord? record = ReadRecord();
        return record is null ? null : Describe(record, installedVersion);
    }

    public Task<OperationResult<SelfUpdateResult>> StartAsync(
        string fromVersion,
        string targetVersion,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            return Task.FromResult(Fail(
                "updater_not_installed",
                "The packaged updater is not installed. Add the Automatic updates feature, or reinstall without REMOVE=Updater."));
        }

        if (_trust(UpdaterPath) is { } untrusted)
        {
            return Task.FromResult(Fail(
                "untrusted_executable",
                $"Refusing to run '{UpdaterPath}' as the service account: {untrusted}."));
        }

        string logPath;
        try
        {
            logPath = Path.Combine(EnsureRecords(), UtcStamp() + ".log");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(Fail("untrusted_path", ex.Message));
        }

        IReadOnlyList<string> arguments =
        [
            "--silent-update",
            "--skip-self-update",
            "--ignore-busy-state",
            "--ignore-postpone",
            "--log-to-file",
            logPath
        ];

        if (dryRun)
        {
            return Task.FromResult(OperationResult<SelfUpdateResult>.Ok(new SelfUpdateResult(
                true,
                "dry_run",
                fromVersion,
                targetVersion,
                null,
                logPath,
                "Would launch " + UpdateEndpoints.UpdaterExecutable + " " + string.Join(' ', arguments),
                null)));
        }

        if (IsInProgress || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Task.FromResult(Fail("update_in_progress", "A Vestigo self-update is already running."));
        }

        try
        {
            if (_updaterRunning())
            {
                return Task.FromResult(Fail("update_in_progress", "A Vestigo self-update is already running."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            int exit;
            try
            {
                exit = _launch(UpdaterPath, arguments, logPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Persist(new SelfUpdateRecord(DateTimeOffset.UtcNow, fromVersion, targetVersion, logPath, null));
                return Task.FromResult(Fail("updater_failed", "The updater could not be started: " + ex.Message));
            }

            SelfUpdateRecord record = new(DateTimeOffset.UtcNow, fromVersion, targetVersion, logPath, exit);
            Persist(record);
            string? tail = ReadLogTail(logPath);
            return Task.FromResult(MapExit(record, tail));
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    internal static string StateFor(int? exitCode, bool? succeeded) =>
        succeeded == true && exitCode is HandedOffExitCode or InstalledExitCode
            ? "succeeded"
            : exitCode switch
            {
                UpToDateExitCode => "up_to_date",
                HandedOffExitCode => "started",
                InstalledExitCode => "succeeded",
                _ => "failed"
            };

    internal static bool? SucceededFor(int? exitCode, string? installedVersion, string targetVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion) &&
            UpdateFeedClient.TryParseVersion(installedVersion, out Version installed) &&
            UpdateFeedClient.TryParseVersion(targetVersion, out Version target) &&
            installed == target)
        {
            return true;
        }

        return exitCode switch
        {
            UpToDateExitCode or InstalledExitCode => true,
            HandedOffExitCode => null,
            null => null,
            _ => false
        };
    }

    private static OperationResult<SelfUpdateResult> MapExit(SelfUpdateRecord record, string? tail)
    {
        if (record.ExitCode == UpToDateExitCode)
        {
            return OperationResult<SelfUpdateResult>.Ok(new SelfUpdateResult(
                false,
                "up_to_date",
                record.FromVersion,
                record.TargetVersion,
                record.ExitCode,
                record.LogPath,
                "The packaged updater reported that Vestigo is already up to date.",
                tail));
        }

        if (record.ExitCode == HandedOffExitCode || record.ExitCode == InstalledExitCode)
        {
            return OperationResult<SelfUpdateResult>.Ok(new SelfUpdateResult(
                false,
                "started",
                record.FromVersion,
                record.TargetVersion,
                record.ExitCode,
                record.LogPath,
                "The updater handed the install to a temporary copy. The service will restart and this MCP session will drop.",
                tail));
        }

        return Fail(
            "updater_failed",
            $"The packaged updater exited with code {record.ExitCode}. See the log at '{record.LogPath}'.",
            record,
            tail);
    }

    private static OperationResult<SelfUpdateResult> Fail(
        string code,
        string message,
        SelfUpdateRecord? record = null,
        string? tail = null) =>
        new(false, record is null
            ? null
            : new SelfUpdateResult(
                false,
                "failed",
                record.FromVersion,
                record.TargetVersion,
                record.ExitCode,
                record.LogPath,
                message,
                tail),
            code,
            message);

    private SelfUpdateAttempt Describe(SelfUpdateRecord record, string? installedVersion)
    {
        bool? succeeded = SucceededFor(record.ExitCode, installedVersion, record.TargetVersion);
        return new SelfUpdateAttempt(
            record.StartedAt,
            record.FromVersion,
            record.TargetVersion,
            record.LogPath,
            record.ExitCode,
            StateFor(record.ExitCode, succeeded),
            succeeded,
            ReadLogTail(record.LogPath));
    }

    private SelfUpdateRecord? ReadRecord()
    {
        if (!File.Exists(RecordPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SelfUpdateRecord>(File.ReadAllText(RecordPath), SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Persist(SelfUpdateRecord record)
    {
        try
        {
            EnsureRecords();
            File.WriteAllText(RecordPath, JsonSerializer.Serialize(record, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The launch already happened; losing the record only hides it from the next check.
        }
    }

    private string EnsureRecords()
    {
        string full = Path.GetFullPath(_recordDirectory);
        string settings = Path.GetFullPath(VestigoPaths.SettingsDirectory);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (full.Equals(settings, comparison) ||
            full.StartsWith(settings + Path.DirectorySeparatorChar, comparison))
        {
            return StateDirectories.EnsureSubdirectory(full);
        }

        Directory.CreateDirectory(full);
        return full;
    }

    private static string UtcStamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");

    internal static string? ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int count = (int)Math.Min(stream.Length, TailBytes);
            if (count == 0)
            {
                return null;
            }

            stream.Seek(-count, SeekOrigin.End);
            byte[] buffer = new byte[count];
            int read = stream.Read(buffer, 0, count);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool UpdaterProcessIsRunning()
    {
        try
        {
            return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpdateEndpoints.UpdaterExecutable)).Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int LaunchUpdater(string executable, IReadOnlyList<string> arguments, string _)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        if (!process.WaitForExit(TimeSpan.FromSeconds(LaunchTimeoutSeconds)))
        {
            throw new InvalidOperationException(
                $"The updater did not exit within {LaunchTimeoutSeconds} seconds.");
        }

        return process.ExitCode;
    }
}
