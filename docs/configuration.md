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
| `Roots` | empty | Named roots (`Name`, absolute `Path`, `Access` flags `Read, Write, Execute`). Clients address them as `name:relative\path` or by an absolute path inside a root. These are separate from the trace-only `AllowedInputRoots` / `AllowedOutputRoots`. |
| `Modules` | all enabled except `Processes`, `Devices`, `RemoteDebugger` and `Desktop` | `Vestigo:Modules:<Name>:Enabled` switches a feature module (`Trace`, `Jobs`, `Files`, `Environment`, `Logs`, `EventLog`, `Desktop`, `Services`, `Software`, `Processes`, `Devices`, `RemoteDebugger`) on or off. Unknown names fail startup. `Processes`, `Devices`, `RemoteDebugger` and `Desktop` are disabled by default. A module whose dependency is disabled fails startup: `Processes` and `Software` need `Jobs`, and `RemoteDebugger` needs `Jobs` and `Processes`. Enabling `Processes`, `Devices`, `RemoteDebugger`, `Desktop` or `Software:AllowUninstall` without any `ApiTokens` is allowed but logs a startup `SECURITY WARNING` naming them (`Services` is named too when the server is bound beyond loopback). The MSI writes this section from its feature tree; see [windows-service.md](windows-service.md#modules). |
| `Devices:NefconPath` | `null` | Absolute path of `nefconc.exe`. Empty uses `tools\nefconc.exe` next to `Vestigo.exe`. |
| `Devices:NefconTimeout` | `00:02:00` | Upper bound for one nefcon run. |
| `Services:ProtectedNames` | `Vestigo`, `RpcSs`, `DcomLaunch`, `PlugPlay`, `Power`, `LSM`, `SamSs` | Service names that mutating tools refuse. Entries you configure are added to the defaults; the defaults cannot be removed. |
| `Software:AllowUninstall` | `false` | Enables `uninstall_software`. Listing remains available when the Software module is enabled. |
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

`AllowedInputRoots` and `AllowedOutputRoots` apply only to ETL, PDB, TMF, and conversion-output paths used by the Trace tools. Named `Roots` apply to Files, Logs, Event Log `.evtx` paths, Processes, Devices INF/driver paths, and the `/files` endpoints.

The installer owns `ListenAddress`, `ListenPort`, `ApiTokens`, `Modules:*:Enabled`, and `Software:AllowUninstall` in the machine settings file. Other keys you add are kept across repair and upgrade.

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
    }
  }
}
```
