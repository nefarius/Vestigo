# Configuration

Machine settings live in `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. On non-Windows development hosts the same relative path is under the user data directory. `VESTIGO_SETTINGS_DIR` overrides the directory for tests.

The installed binaries stay in Program Files. The installer does not ship `appsettings.Production.json`, so a machine file survives upgrades.

Property defaults are generated in [generated/configuration.md](generated/configuration.md).

| Setting | Default | Purpose |
| --- | --- | --- |
| `ListenAddress` | `127.0.0.1` | IP address to bind: loopback (`127.0.0.1`, `::1`) or any other address such as `0.0.0.0` or `::` to accept network clients. Host names are rejected, as is `Kestrel:Endpoints`. Binding beyond loopback without `ApiTokens` logs a startup warning. |
| `ListenPort` | `5098` | HTTP port. Changing it requires a service restart. |
| `ApiTokens` | empty | Bearer tokens. Empty means anonymous `/mcp`; strongly recommended whenever `ListenAddress` is not loopback. |
| `AllowedInputRoots` | empty | Roots for ETL, PDB, and TMF paths. |
| `AllowedOutputRoots` | empty | Roots for conversion output. |
| `Roots` | empty | Named roots (`Name`, absolute `Path`, `Access` flags `Read, Write, Execute`). Clients address them as `name:relative\path` or by an absolute path inside a root. These are separate from the trace-only `AllowedInputRoots` / `AllowedOutputRoots`. Names such as `drive-c` are reserved only when `VolumeRoots` is not `None`; names like `drive-backups` stay available. |
| `VolumeRoots` | `None` | Adds lettered drive volumes as named roots (`drive-c`, `drive-d`, …) that are merged with `Roots`. `None` uses only `Roots`. `SystemDrive` is the Windows directory's drive (`SpecialFolder.Windows`, then `%SystemDrive%`). `FixedVolumes` is every ready `DriveType.Fixed` volume (the MSI default on a fresh install). `AllVolumes` also includes ready removable, network, CD-ROM, and RAM volumes present at service start. `AllVolumesLive` is the same set, re-enumerated on demand with a two-second cache so volumes that appear or leave while the service is running are included. UNC paths are never added automatically. The service runs as LocalSystem, so per-user mapped network drives are not visible; only system-wide mounts appear. |
| `VolumeRootAccess` | `Read, Write, Execute` | Access granted to every volume root produced by `VolumeRoots`. Must not be empty when `VolumeRoots` is not `None`. |
| `Modules` | all enabled except `Processes`, `Devices`, `RemoteDebugger`, `Desktop`, `Audio` and `DebugOutput` | `Vestigo:Modules:<Name>:Enabled` switches a feature module (`Trace`, `Jobs`, `Files`, `Environment`, `System`, `Logs`, `EventLog`, `Desktop`, `Audio`, `Services`, `Software`, `Processes`, `Devices`, `RemoteDebugger`, `DebugOutput`) on or off. Unknown names fail startup. `Processes`, `Devices`, `RemoteDebugger`, `Desktop`, `Audio` and `DebugOutput` are disabled by default. A module whose dependency is disabled fails startup: `Processes` and `Software` need `Jobs`, and `RemoteDebugger` needs `Jobs` and `Processes`. Enabling `Processes`, `Devices`, `RemoteDebugger`, `Software:AllowUninstall`, or `System:AllowBootConfigurationChanges` without any `ApiTokens` is allowed but logs a startup `SECURITY WARNING` naming them (`Services` is named too when the server is bound beyond loopback). Enabling `Desktop` without tokens logs the same warning for window titles (and for screenshots when `Desktop:AllowScreenshots` is also on). Enabling `Audio` without tokens logs the same warning for audio devices and the processes using them (and for changing defaults when `Audio:AllowSetDefault` is also on). Enabling `DebugOutput` without tokens logs the same warning for its machine-wide `OutputDebugString` reader, separately from those administrator-equivalent actions. The MSI writes this section from its feature tree; see [windows-service.md](windows-service.md#modules). |
| `Devices:NefconPath` | `null` | Absolute path of `nefconc.exe`. Empty uses `tools\nefconc.exe` next to `Vestigo.exe`. |
| `Devices:NefconTimeout` | `00:02:00` | Upper bound for one nefcon run. |
| `DebugOutput:ExecutablePath` | `null` | Absolute path of `DebugViewConsole.exe`. Empty uses `tools\DebugViewConsole.exe` next to `Vestigo.exe`. |
| `DebugOutput:ExpectedSha256` | pinned 1.30.0.0 digest | Lowercase SHA-256 of the executable that may be launched. Empty skips the check (for an administrator-overridden path). |
| `DebugOutput:MaxDuration` | `01:00:00` | Upper bound for one debug-output capture. |
| `DebugOutput:MaxMessages` | `1000000` | Maximum messages written to one debug-output spool. |
| `DebugOutput:MaxOutputBytes` | `536870912` | Maximum bytes written to one debug-output spool. |
| `DebugOutput:MaxFilterPatterns` | `16` | Maximum capture-time include and exclude patterns combined. |
| `DebugOutput:MaxPatternLength` | `256` | Maximum characters in one capture-time filter pattern. |
| `Services:ProtectedNames` | `Vestigo`, `RpcSs`, `DcomLaunch`, `PlugPlay`, `Power`, `LSM`, `SamSs` | Service names that mutating tools refuse. Entries you configure are added to the defaults; the defaults cannot be removed. |
| `Desktop:AllowScreenshots` | `false` | Enables `take_screenshot`. Window and display listing remains available when the Desktop module is enabled. |
| `Desktop:MaxScreenshotBytes` | `134217728` | Upper bound for the session-helper JSON that carries the PNG (including base64). Minimum `1048576`, maximum `536870912`. |
| `Audio:AllowSetDefault` | `false` | Enables `set_default_audio_endpoint`. Listing remains available when the Audio module is enabled. |
| `Audio:MeterSampleMilliseconds` | `200` | How long `list_audio_endpoints` samples the peak meter per endpoint. `0` skips the meter. Maximum `2000`. |
| `Software:AllowUninstall` | `false` | Enables `uninstall_software`. Listing remains available when the Software module is enabled. |
| `System:AllowBootConfigurationChanges` | `false` | Enables `set_test_signing` and `set_whql_developer_test_mode`. Reads remain available when the System module is enabled. Changes take effect after a reboot. |
| `Software:ProtectedNames` | `Vestigo` | Display-name substrings that uninstall refuses. Configured names are added to the defaults. |
| `Software:MaxUninstallDuration` | `00:30:00` | Upper bound for one uninstall; the process tree is killed afterwards. |
| `Processes:AllowPathLookup` | `true` | Lets `run_process` resolve bare names such as `cmd.exe` from the service `PATH`. When `false`, only files under roots with `Execute` access can be started. |
| `RemoteDebugger:DefaultPort` | `4026` | Port `start_remote_debugger` uses when the caller gives none. |
| `RemoteDebugger:InstallerDownloadTimeout` | `00:10:00` | Upper bound for the installer download inside the install job. |
| `RemoteDebugger:ExtraSearchPaths` | empty | Absolute directories probed for `msvsmon.exe` (directly or as `<dir>\<arch>\msvsmon.exe`); a hit counts only when the file major version is 17 or 18 for the requested version. |
| `MaxConcurrentJobs` | 8 | Running background job limit. |
| `MaxJobOutputBytes` | 67108864 | Output kept per job; later output is dropped with a marker. |
| `JobRetention` | `1.00:00:00` | How long finished jobs and their output are kept. |
| `MaxJobReadBytes` | 65536 | Maximum bytes returned by one `read_job_output` call. |
| `Files:MaxUploadBytes` | 4294967296 | Largest accepted `PUT /files` upload or `fetch_file` download. Larger uploads get HTTP 413; larger downloads fail the job with `too_large`. The partial file is discarded in both cases. |
| `Files:TicketLifetime` | 00:15:00 | How long a one-time `/files` transfer ticket stays valid. Must be positive and at most 24 hours. Only used when `ApiTokens` is set. |
| `Environment:ProtectedNames` | `COMSPEC`, `SystemRoot`, `windir` | Variable names (case-insensitive) that `set_environment_variable` refuses to set or unset in any scope. |
| `Logs:MaxTailLines` | 2000 | Maximum lines returned by one `tail_text_file` or `read_text_file_since` call. |
| `Logs:MaxTailBytes` | 1048576 | Maximum bytes examined by one call; a longer single line is cut. |
| `EventLog:MaxEvents` | 500 | Maximum events returned by one `query_event_log` call. |
| `EventLog:MaxScannedEvents` | 20000 | Maximum events examined per query, which bounds message text filters. |
| `SymbolServer` | null | Optional HTTP symbol server. Explicit values bypass `_NT_SYMBOL_PATH`. |
| `SymbolCache` | null | Cache directory. The default is `symcache` under the settings directory. A cache outside that tree is not verified: allow write access for the trusted identity that runs Vestigo (the MSI service account, or the user running `dotnet run`), and do not share it between a privileged service and an untrusted user. |
| `MaxInputBytes` | 4294967296 | Maximum size of one input file. |
| `MaxEvents` | 1000000 | Maximum decoded events per conversion or capture. |
| `MaxOutputBytes` | 536870912 | Maximum bytes written per conversion or capture. |
| `MaxCaptureDuration` | `01:00:00` | Upper bound for a realtime capture. |
| `SpoolRetention` | `1.00:00:00` | How long a stopped spool is kept. |
| `MaxConcurrentCaptures` | 4 | Running capture limit. |
| `MaxReadBatch` | 200 | Maximum events returned by one `read_capture` call. |
| `BufferSizeKb` | 64 | ETW buffer size. |
| `MaxReadWait` | 00:00:30 | Longest `read_capture` long-poll (0 to 2 minutes). |
| `AttachableSessions` | `[]` | Exact names or `*` globs of external ETW sessions `attach_etw_session` may read. Empty disables attach. |
| `FlushTimerSeconds` | 1 | ETW flush interval; bounds live latency for managed captures. Attached sessions keep their owner's settings. |
| `ClockResolution` | `QueryPerformanceCounter` | `QueryPerformanceCounter`, `SystemTime`, or `CpuCycle`. |

Serilog writes console output and daily files under the settings `logs` directory (`vestigo-.log`). The default level is Information. The host keeps 14 rolled files.

`AllowedInputRoots` and `AllowedOutputRoots` apply only to ETL, PDB, TMF, and conversion-output paths used by the Trace tools. Named `Roots` and any `VolumeRoots` apply to Files, Logs, Event Log `.evtx` paths, Processes, Devices INF/driver paths, and the `/files` endpoints.

The installer owns `ListenAddress`, `ListenPort`, `ApiTokens`, `VolumeRoots`, `VolumeRootAccess`, `Modules:*:Enabled`, `Software:AllowUninstall`, `Audio:AllowSetDefault`, `System:AllowBootConfigurationChanges`, and `Desktop:AllowScreenshots` in the machine settings file. Other keys you add, including hand-written `Roots`, are kept across repair and upgrade.

## Examples

Loopback development (`dotnet run` default shape), with a trace drop and a writable named root:

```json
{
  "Vestigo": {
    "ListenAddress": "127.0.0.1",
    "ListenPort": 5098,
    "ApiTokens": [],
    "AllowedInputRoots": [ "D:\\traces" ],
    "AllowedOutputRoots": [ "D:\\traces\\out" ],
    "VolumeRoots": "None",
    "Roots": [
      { "Name": "drops", "Path": "D:\\drops", "Access": "Read, Write" }
    ]
  }
}
```

Authenticated remote access with opt-in modules. Retrieve a generated MSI token from the same file after install:

```json
{
  "Vestigo": {
    "ListenAddress": "0.0.0.0",
    "ListenPort": 5098,
    "ApiTokens": [ "<generated-or-chosen-token>" ],
    "AllowedInputRoots": [ "D:\\traces" ],
    "AllowedOutputRoots": [ "D:\\traces\\out" ],
    "Roots": [
      { "Name": "drivers", "Path": "D:\\drivers", "Access": "Read, Write, Execute" }
    ],
    "Modules": {
      "Processes": { "Enabled": true },
      "Devices": { "Enabled": true }
    },
    "Software": {
      "AllowUninstall": false
    },
    "Desktop": {
      "AllowScreenshots": false
    },
    "Audio": {
      "AllowSetDefault": false
    },
    "System": {
      "AllowBootConfigurationChanges": false
    }
  }
}
```
