# Configuration

Machine settings live in `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. On non-Windows development hosts the same relative path is under the user data directory. `VESTIGO_SETTINGS_DIR` overrides the directory for tests.

The installed binaries stay in Program Files. The installer does not ship `appsettings.Production.json`, so a machine file survives upgrades.

Property defaults are generated in [generated/configuration.md](generated/configuration.md).

## Local configuration page

Open `http://localhost:5098/config` on the machine running Vestigo, using its configured port. A loopback IP such as `127.0.0.1` or `[::1]` also works when that address is bound. A listener bound only to a non-loopback IP must be changed to loopback or a wildcard in the JSON file before the page is reachable.

The MudBlazor page groups every writable `Vestigo` option, including disabled modules and their gates. It provides switches, enum selectors, text fields and one-entry-per-line string lists. Named roots and other structured lists use JSON fields; flag enums accept comma-separated names. Durations use `hh:mm:ss`. Secret fields are masked; API-token lists are visible to local users. Plugin-specific values already in the machine file are retained and editable. The page does not configure Windows service identity, firewall rules or other MSI installation choices outside `Vestigo` settings.

The initial form shows startup values overlaid with the saved machine settings, so it can show pending changes before restart. **Save settings** validates using the host's options and module dependency checks, preserves other JSON sections and unknown plugin settings, and atomically replaces the machine file as UTF-8 without BOM. Saving materializes the displayed defaults in the file and removes JSON comments/formatting. A revision check rejects another editor's intervening save; **Reload saved settings** discards unsaved edits. Restart is required: there is no partial hot reload.

**Restart service** asks for confirmation, starts a separate helper, and gracefully stops the host before the helper starts the `Vestigo` Windows service again. Active jobs, captures and MCP/browser connections are interrupted. Refresh after restart; if the address or port changed, use the new local URL. For a console host, restart the process manually. Plugin-loading changes can require manual recovery if the deployment is invalid; plugin startup is not executed during form validation.

This is local administration without an API token, including when `/mcp` requires one. Any local user or process that can connect can read credentials and change administrator-equivalent settings. Remote connections receive HTTP 403. The page, static UI assets and Blazor transport enforce loopback peer and Host/port checks, reject forwarded headers and cross-site origins, disable caching and framing, and use the Blazor antiforgery/circuit protections. Never proxy `/config`, `/_blazor`, `/_framework` or `/_content`: a proxy that hides its remote origin can defeat a loopback-only trust boundary. See [security](security.md#local-configuration).

| Setting | Default | Purpose |
| --- | --- | --- |
| `ListenAddress` | `127.0.0.1` | IP address to bind: loopback (`127.0.0.1`, `::1`) or any other address such as `0.0.0.0` or `::` to accept network clients. Host names are rejected, as is `Kestrel:Endpoints`. Binding beyond loopback without `ApiTokens` logs a startup warning. |
| `ListenPort` | `5098` | HTTP port. Changing it requires a service restart. |
| `ApiTokens` | empty | Bearer tokens. Empty means anonymous `/mcp`; strongly recommended whenever `ListenAddress` is not loopback. |
| `AllowedInputRoots` | empty (uses `<settings>\traces`) | Roots for ETL, PDB, TMF, and uploaded zip paths. Empty means the built-in folder `%ProgramData%\Nefarius Software Solutions\Vestigo\traces` (or `traces` under `VESTIGO_SETTINGS_DIR`). Configured values replace that default; they are not merged with it. Do not put a whole volume here: symbol lookup searches each input root recursively for `*.pdb`. |
| `AllowedOutputRoots` | empty (uses `<settings>\traces`) | Roots for conversion output and archive extraction. Empty uses the same built-in `traces` folder. |
| `Roots` | empty | Named roots (`Name`, absolute `Path`, `Access` flags `Read, Write, Execute`). Clients address them as `name:relative\path` or by an absolute path inside a root. These are separate from the trace-only `AllowedInputRoots` / `AllowedOutputRoots`. Names such as `drive-c` are reserved only when `VolumeRoots` is not `None`; names like `drive-backups` stay available. |
| `VolumeRoots` | `None` | Adds lettered drive volumes as named roots (`drive-c`, `drive-d`, …) that are merged with `Roots`. `None` uses only `Roots`. `SystemDrive` is the Windows directory's drive (`SpecialFolder.Windows`, then `%SystemDrive%`). `FixedVolumes` is every ready `DriveType.Fixed` volume (the MSI default on a fresh install). `AllVolumes` also includes ready removable, network, CD-ROM, and RAM volumes present at service start. `AllVolumesLive` is the same set, re-enumerated on demand with a two-second cache so volumes that appear or leave while the service is running are included. UNC paths are never added automatically. The service runs as LocalSystem, so per-user mapped network drives are not visible; only system-wide mounts appear. |
| `VolumeRootAccess` | `Read, Write, Execute` | Access granted to every volume root produced by `VolumeRoots`. Must not be empty when `VolumeRoots` is not `None`. |
| `Modules` | all enabled except `Processes`, `Devices`, `RemoteDebugger`, `Desktop`, `Input`, `Audio`, `DebugOutput`, `Diagnostics`, `Registry` and `WindowsServicing` | `Vestigo:Modules:<Name>:Enabled` switches a feature module (`Trace`, `Jobs`, `Files`, `Environment`, `System`, `Power`, `Logs`, `EventLog`, `Desktop`, `Input`, `Audio`, `Services`, `ScheduledTasks`, `Software`, `Processes`, `Devices`, `RemoteDebugger`, `DebugOutput`, `Diagnostics`, `Registry`, `WindowsServicing`, `Updates`) on or off. Unknown names fail startup. `Processes`, `Devices`, `RemoteDebugger`, `Desktop`, `Input`, `Audio`, `DebugOutput`, `Diagnostics`, `Registry` and `WindowsServicing` are disabled by default. A module whose dependency is disabled fails startup: `Processes`, `Software`, `Diagnostics`, and `WindowsServicing` need `Jobs`, `Input` needs `Desktop`, and `RemoteDebugger` needs `Jobs` and `Processes`. Enabling `Processes`, `Devices`, `RemoteDebugger`, `Software:AllowUninstall`, `ScheduledTasks:AllowChanges`, `System:AllowBootConfigurationChanges`, `System:AllowUacChanges`, or `Updates:AllowSelfUpdate`, or `Input:AllowInput` (with the Input module on) without any `ApiTokens` is allowed but logs a startup `SECURITY WARNING` naming them (`Services` is named too when the server is bound beyond loopback). Enabling `Desktop` without tokens logs the same warning for window titles (and for screenshots when `Desktop:AllowScreenshots` is also on). Enabling `Audio` without tokens logs the same warning for audio devices and the processes using them (and for changing defaults when `Audio:AllowSetDefault` is also on). Enabling `DebugOutput` without tokens logs the same warning for its machine-wide `OutputDebugString` reader, separately from those administrator-equivalent actions. Enabling `Diagnostics` without tokens logs the same warning for PE inspection, signature checks, loaded modules, SetupAPI logs, device problem codes, graphics adapters, TCP and UDP bindings owned by a process, Media Foundation video encoders and camera formats, audio format checks, and Windows Error Reporting report metadata (online revocation when `Diagnostics:AllowOnlineRevocation` is also on, capture probes when `Diagnostics:AllowCaptureProbes` is also on, network probes when `Diagnostics:AllowNetworkProbes` is also on, audio playback when `Diagnostics:AllowAudioPlayback` is also on, process dumps when `Diagnostics:AllowProcessDumps` is also on, and kernel dump staging when `Diagnostics:AllowKernelDumpStaging` is also on). Enabling `WindowsServicing` without tokens warns about queue paths, history, package metadata and root-confined logs. Enabling `Registry` without tokens warns that clients can read values under the allowed prefixes. The MSI owns `Modules:WindowsServicing:Enabled`; it preserves hand-written `WindowsServicing` limit settings. The MSI writes this section from its feature tree; see [windows-service.md](windows-service.md#modules). |
| `Devices:NefconPath` | `null` | Absolute path of `nefconc.exe`. Empty uses `tools\nefconc.exe` next to `Vestigo.exe`. |
| `Devices:NefconTimeout` | `00:02:00` | Upper bound for one nefcon run. |
| `DebugOutput:ExecutablePath` | `null` | Absolute path of `DebugViewConsole.exe`. Empty uses `tools\DebugViewConsole.exe` next to `Vestigo.exe`. |
| `DebugOutput:ExpectedSha256` | pinned 1.30.0.0 digest | Lowercase SHA-256 of the executable that may be launched. Empty skips the check (for an administrator-overridden path). |
| `DebugOutput:MaxDuration` | `01:00:00` | Upper bound for one debug-output capture. |
| `DebugOutput:MaxMessages` | `1000000` | Maximum messages written to one debug-output spool. |
| `DebugOutput:MaxOutputBytes` | `536870912` | Maximum bytes written to one debug-output spool. |
| `DebugOutput:MaxFilterPatterns` | `16` | Maximum capture-time include and exclude patterns combined. |
| `DebugOutput:MaxPatternLength` | `256` | Maximum characters in one capture-time filter pattern. |
| `Diagnostics:AllowOnlineRevocation` | `false` | Allows `verify_file_signature` with `revocation: online`, and online TLS revocation in `test_endpoint`. Both may contact revocation servers. Signature modes `none` and `cacheOnly` never use the network. A TLS chain can still download a missing issuer certificate from an Authority Information Access URL. |
| `Diagnostics:AllowCaptureProbes` | `false` | Enables `probe_capture_support`. The tool is registered with Diagnostics but returns `probes_disabled` until this is true. The MSI exposes it as an unchecked child of Diagnostics. |
| `Diagnostics:AllowNetworkProbes` | `false` | Enables `test_endpoint`. The tool returns `network_probes_disabled` until this is true. The destination must also be listed in `AllowedProbeDestinations`. The MSI exposes the gate as an unchecked child of Diagnostics. |
| `Diagnostics:AllowAudioPlayback` | `false` | Allows `probe_audio_format` with `playback: true` to render a synthetic tone. Format checks do not use this gate and do not emit sound. The MSI exposes it as an unchecked child of Diagnostics. |
| `Diagnostics:AllowProcessDumps` | `false` | Enables `capture_process_dump`. The tool returns `process_dumps_disabled` until this is true. The MSI exposes it as an unchecked child of Diagnostics. |
| `Diagnostics:AllowDriverVerifier` | `false` | Enables `set_driver_verifier`. `reset_driver_verifier` stays available while this is false. The MSI exposes it as an unchecked child of Diagnostics. Vestigo does not reboot. |
| `Diagnostics:AllowFirewallInventory` | `false` | Enables `get_firewall_profiles` and `list_firewall_rules`. Both return `firewall_inventory_disabled` until this is true. The MSI exposes it as an unchecked child of Diagnostics. |
| `Diagnostics:AllowKernelDumpStaging` | `false` | Enables `stage_kernel_dump`. The tool returns `kernel_dump_staging_disabled` until this is true. The MSI exposes it as an unchecked child of Diagnostics. |
| `Diagnostics:AllowedProbeDestinations` | empty | Hosts, or `host:port` entries, that `test_endpoint` may contact. Empty allows none. Wildcards and ranges are rejected. The installer does not own this list, so a hand-written value survives repair. |
| `Diagnostics:MaxProcessModules` | `500` | Upper bound for `list_process_modules` `limit`. Minimum `1`, maximum `5000`. |
| `Diagnostics:MaxInstallHistoryBytes` | `16777216` | Maximum SetupAPI log bytes read for one `get_device_install_history` or `explain_device_problem` request. Newer bytes are kept. Minimum `65536`, maximum `268435456`. |
| `Diagnostics:MaxProbeDuration` | `00:00:05` | Upper bound for one capture probe. Minimum `00:00:00.100`, maximum `00:00:30`. |
| `Diagnostics:MaxProcessConnections` | `500` | Upper bound for `list_process_connections` `limit`. Minimum `1`, maximum `5000`. |
| `Diagnostics:MaxEndpointProbeDuration` | `00:00:05` | Upper bound for one `test_endpoint` call. Minimum `00:00:00.100`, maximum `00:00:30`. |
| `Diagnostics:MaxUdpPayloadBytes` | `64` | Maximum bytes in one `test_endpoint` UDP datagram, including a STUN binding request. Minimum 1, maximum 64. |
| `Diagnostics:MaxUdpProbeDuration` | `00:00:05` | Upper bound for a `test_endpoint` UDP send or STUN wait. Minimum `00:00:00.100`, maximum `00:00:05`. |
| `Diagnostics:MaxVideoEncoders` | `200` | Upper bound for `list_video_encoders` `limit`. Minimum `1`, maximum `1000`. |
| `Diagnostics:MaxCameraFormats` | `64` | Upper bound for `list_camera_formats` `limit`. Minimum `1`, maximum `500`. |
| `Diagnostics:MaxAudioProbeDuration` | `00:00:02` | Upper bound for one audible `probe_audio_format` call. Minimum `00:00:00.100`, maximum `00:00:05`. |
| `Diagnostics:MaxCrashReports` | `200` | Upper bound for `list_crash_reports` `limit`. Minimum `1`, maximum `2000`. |
| `Diagnostics:MaxFirewallRules` | `200` | Upper bound for `list_firewall_rules` `limit`. Minimum `1`, maximum `2000`. |
| `Diagnostics:MaxCrashReportFileBytes` | `262144` | Maximum bytes read from one `Report.wer`. Minimum `4096`, maximum `1048576`. |
| `Diagnostics:MaxDumpBytes` | `536870912` | Maximum dump file size. A larger file is deleted. Minimum `16777216`, maximum `4294967296`. |
| `Diagnostics:MinDumpFreeBytes` | `1073741824` | Free bytes that must remain on the output volume after reserving `MaxDumpBytes`. Minimum `67108864`, maximum `17179869184`. |
| `Diagnostics:MaxInspectBytes` | `268435456` | Maximum file size accepted by `inspect_binary`. Minimum `1048576`, maximum `2147483648`. |
| `Services:ProtectedNames` | `Vestigo`, `RpcSs`, `DcomLaunch`, `PlugPlay`, `Power`, `LSM`, `SamSs` | Service names that mutating tools refuse. Entries you configure are added to the defaults; the defaults cannot be removed. |
| `Desktop:AllowScreenshots` | `false` | Enables `take_screenshot`, `inspect_screen` and `find_on_screen`. Window and display listing remains available when the Desktop module is enabled. |
| `Desktop:AllowWindowPlacement` | `false` | Enables `set_window_client_rect` and `restore_window_placement`. |
| `Desktop:AllowDisplayLayoutChanges` | `false` | Enables `start_display_layout_transaction` and `restore_display_layout_transaction`. Does not create virtual displays. |
| `Desktop:AllowDesktopSessions` | `false` | Enables `create_desktop_session`, `activate_desktop_session`, `restore_desktop_session`, and `close_desktop_session`. Does not open the lock screen or UAC desktop. |
| `Desktop:MaxDesktopSessions` | `4` | Most open managed desktops. Range 1 to 8. |
| `Desktop:DesktopSessionLease` | `00:30:00` | How long an unreleased managed desktop may stay open. 15 seconds to 2 hours. Lease expiry restores the input desktop and stops only processes launched into that session. |
| `Desktop:MaxWindowEventCaptures` | `2` | Most window-event captures at once. Range 1 to 8. |
| `Desktop:MaxWindowEvents` | `20000` | Most records stored for one window-event capture. Range 1 to 100000. |
| `Desktop:MaxWindowEventBytes` | `16777216` | Largest spool for one window-event capture. Range 65536 to 134217728. |
| `Desktop:MaxWindowEventDuration` | `00:10:00` | Longest window-event capture. Positive, at most one hour. |
| `Desktop:WindowEventRetention` | `06:00:00` | How long a finished window-event capture is kept. Positive, at most 7 days. |
| `Desktop:DisplayTransactionLease` | `00:10:00` | How long an unreleased display-layout transaction may stay applied. 15 seconds to 2 hours. |
| `Desktop:DisplayObservationIntervalMilliseconds` | `200` | Delay between display-topology samples. Range 50 to 2000. |
| `Desktop:MaxScreenshotBytes` | `134217728` | Upper bound for the session-helper JSON that carries the PNG (including base64). Minimum `1048576`, maximum `536870912`. |
| `Input:AllowInput` | `false` | Enables `send_input`, which injects mouse and keyboard input as the console user. Needs the Input module, which needs the Desktop module. Administrator-equivalent when an elevated window is open. |
| `Input:MaxActions` | `50` | Largest number of actions in one `send_input` call. Minimum `1`, maximum `200`. |
| `Input:MaxWaitMilliseconds` | `10000` | Largest total of `wait` actions in one call. Minimum `0`, maximum `60000`. |
| `Input:MaxTypedCharacters` | `2000` | Largest total of characters across `type` actions in one call. Minimum `1`, maximum `8000`. |
| `Audio:AllowSetDefault` | `false` | Enables `set_default_audio_endpoint`. Listing remains available when the Audio module is enabled. |
| `Audio:MeterSampleMilliseconds` | `200` | How long `list_audio_endpoints` samples the peak meter per endpoint. `0` skips the meter. Maximum `2000`. |
| `ScheduledTasks:AllowChanges` | `false` | Enables `run_scheduled_task`, `stop_scheduled_task`, `enable_scheduled_task`, `disable_scheduled_task` and `delete_scheduled_task`. Listing, inspecting, and history remain available when the ScheduledTasks module is enabled. |
| `ScheduledTasks:ProtectedPaths` | `\Microsoft\Windows\` | Case-insensitive task path prefixes that delete and disable refuse. Entries must start with a backslash. Entries you configure are added to the defaults; the defaults cannot be removed. |
| `Software:AllowUninstall` | `false` | Enables `uninstall_software`. Listing remains available when the Software module is enabled. |
| `System:AllowBootConfigurationChanges` | `false` | Enables `set_test_signing` and `set_whql_developer_test_mode`. Reads remain available when the System module is enabled. Changes take effect after a reboot. |
| `System:AllowUacChanges` | `false` | Enables `set_uac`. `get_uac` remains available when the System module is enabled. Turning UAC off takes effect after a reboot. |
| `Updates:AllowSelfUpdate` | `false` | Enables `start_self_update`. `check_for_updates` remains available when the Updates module is enabled. Needs the MSI Automatic updates feature so the packaged Vicius updater is present. |
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
| `Logs:RipgrepPath` | null | Absolute path of `rg.exe` for `search_text`. Empty uses `tools\rg.exe` next to Vestigo.exe. |
| `Logs:RipgrepSha256` | pinned ripgrep 15.2.0 digest | SHA-256 of the `rg.exe` that may be launched, checked before every search. Empty skips the check; use that only for a path you replaced on purpose. |
| `Logs:MaxSearchResults` | 1000 | Maximum lines (matches plus context) returned by one `search_text` call; ripgrep is stopped when it is reached. |
| `Logs:MaxSearchContextLines` | 10 | Maximum context lines requested before or after each match. |
| `Logs:MaxSearchLineLength` | 1000 | Longest line text returned; longer lines are cut to a window around the first match. At least 80. |
| `Logs:MaxSearchDuration` | 00:01:00 | Longest time one search may run before ripgrep is stopped and partial results are returned. |
| `Logs:MaxConcurrentSearches` | 2 | Searches that may run at once; further calls wait briefly and then fail with `search_busy`. |
| `Logs:MaxJsonBytes` | 16777216 | Largest UTF-8 JSON document `read_json_path` or `get_json_summary` will read. A caller `maxBytes` can only lower this cap. |
| `Logs:MaxJsonValueBytes` | 262144 | Largest matched value returned inline. One larger match fails with `value_too_large`; a larger wildcard match is omitted. |
| `Logs:MaxJsonMatches` | 100 | Most matches, top-level keys, or summary members returned by one JSON call. |
| `Registry:AllowedPrefixes` | empty | Key prefixes `read_registry_value` may open. Empty uses the built-in Windows locations. A non-empty list replaces that set. The installer does not own this list. |
| `Registry:DeniedPrefixes` | `HKLM\SAM`, `HKLM\SECURITY`, `HKLM\SYSTEM\CurrentControlSet\Control\Lsa`, `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` | Prefixes that are never read. Configured entries are added to these defaults, and a denied prefix wins over an allowed one. |
| `Registry:MaxEntries` | 500 | Most keys, values, and subkey names returned by one `list_registry_values` call. |
| `Registry:MaxRecursionDepth` | 4 | Deepest nested key a list may open. Zero lists only the requested key. |
| `Registry:MaxBinaryBytes` | 4096 | Largest `REG_BINARY` that is read or hashed. A larger value fails before it is allocated. |
| `Registry:MaxValueStringLength` | 4096 | Longest string value returned, in characters. |
| `EventLog:MaxEvents` | 500 | Maximum events returned by one `query_event_log` call. |
| `EventLog:MaxScannedEvents` | 20000 | Maximum events examined per query, which bounds message text filters. |
| `SymbolServer` | null | Optional HTTP symbol server. Explicit values bypass `_NT_SYMBOL_PATH`. A `user:pass@` prefix is converted to `SymbolServerAuth` Basic and stripped from the URL. A query string logs a startup warning; put API keys in `SymbolServerAuth` instead. `get_trace_settings` reports the URL with userinfo and the query string removed. |
| `SymbolCache` | null | Cache directory. The default is `symcache` under the settings directory. A cache outside that tree is not verified: allow write access for the trusted identity that runs Vestigo (the MSI service account, or the user running `dotnet run`), and do not share it between a privileged service and an untrusted user. PDB files under the resolved cache (including an environment-selected cache) may be passed to Trace tools. |
| `SymbolServerAuth:Scheme` | `None` | `None`, `Basic`, `Bearer`, or `Header`. Requires an explicit `SymbolServer`. HTTPS is required except when the server is loopback. |
| `SymbolServerAuth:HeaderName` | null | Header name when Scheme is `Header` (for example `X-Api-Key`). |
| `SymbolServerAuth:Username` | null | User name when Scheme is `Basic`. |
| `SymbolServerAuth:Secret` | null | Inline secret. Mutually exclusive with `SecretFile`. Never logged. |
| `SymbolServerAuth:SecretFile` | null | Absolute path of a file whose contents are the secret, read once at startup. |
| `MaxArchiveEntries` | 10000 | Upper bound for entries in one `extract_trace_archive` zip. |
| `MaxInputBytes` | 4294967296 | Maximum size of one input file, and the uncompressed total extracted from one archive. |
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

`AllowedInputRoots` and `AllowedOutputRoots` apply only to ETL, PDB, TMF, zip, and conversion-output paths used by the Trace tools. When both lists are empty, Vestigo also registers a named root `traces` (Read, Write) at that folder so `get_upload_url traces:driver.pdb` works in every `VolumeRoots` mode. A configured root already named `traces` wins and a startup warning is logged. Named `Roots` and any `VolumeRoots` apply to Files, Logs, Event Log `.evtx` paths, Processes, Devices INF/driver paths, binaries for `resolve_binary_symbols`, and the `/files` endpoints.

The installer owns `ListenAddress`, `ListenPort`, `ApiTokens`, `VolumeRoots`, `VolumeRootAccess`, `Modules:*:Enabled`, `Software:AllowUninstall`, `ScheduledTasks:AllowChanges`, `Audio:AllowSetDefault`, `System:AllowBootConfigurationChanges`, `System:AllowUacChanges`, `Desktop:AllowScreenshots`, `Desktop:AllowWindowPlacement`, `Desktop:AllowDisplayLayoutChanges`, `Desktop:AllowDesktopSessions`, `Input:AllowInput`, `Diagnostics:AllowCaptureProbes`, `Diagnostics:AllowNetworkProbes`, `Diagnostics:AllowAudioPlayback`, `Diagnostics:AllowProcessDumps`, `Diagnostics:AllowFirewallInventory`, `Diagnostics:AllowDriverVerifier`, `Diagnostics:AllowKernelDumpStaging`, and `Updates:AllowSelfUpdate` in the machine settings file. Other keys you add, including hand-written `Roots`, are kept across repair and upgrade.

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
    "ScheduledTasks": {
      "AllowChanges": false
    },
    "Desktop": {
      "AllowScreenshots": false,
      "AllowWindowPlacement": false,
      "AllowDisplayLayoutChanges": false,
      "AllowDesktopSessions": false
    },
    "Input": {
      "AllowInput": false,
      "MaxActions": 50,
      "MaxWaitMilliseconds": 10000,
      "MaxTypedCharacters": 2000
    },
    "Audio": {
      "AllowSetDefault": false
    },
    "System": {
      "AllowBootConfigurationChanges": false,
      "AllowUacChanges": false
    },
    "Updates": {
      "AllowSelfUpdate": false
    }
  }
}
```

## Windows servicing

The opt-in `WindowsServicing` module requires Jobs. See [Windows servicing diagnostics](windows-servicing.md)
for tools, data disclosure, Read roots and cancellation limits. It is separate from Vestigo self-updates.

| Setting | Default | Meaning |
| --- | --- | --- |
| `WindowsServicing:MaxResults` | 200 | Returned/queried entries per source (1 to 2000). |
| `WindowsServicing:MaxLogBytes` | 262144 | Bytes examined per fixed log tail (16 to 4194304); Logs caps also apply. |
| `WindowsServicing:MaxLogLines` | 200 | Lines returned per fixed log tail (1 to 2000); Logs caps also apply. |
| `WindowsServicing:NativeCallTimeout` | `00:02:00` | Maximum wait for native diagnostics (positive, at most 30 minutes). A timed-out call retains its concurrency slot until completion. |

## External plugins

`Vestigo:Plugins:Enabled` defaults to `false`. `Plugins:TrustMode` defaults to `Disabled`; explicitly select `AdministratorManaged` or `PinnedSha256` to enable loading. `Plugins:Root` is an optional absolute protected root; its default is the settings directory's `plugins` subfolder. `Plugins:PackageHashes` maps plugin IDs to complete deployment SHA-256 pins. External modules always require explicit `Modules:<Name>:Enabled=true`. These administrator-owned keys are preserved by MSI settings merge and are not selected by the built-in module feature tree. See [plugins](plugins.md) for manifest schema and deployment examples.

API 1.1 provider prefixes and `RequiresSessionIdentity` are provider contract properties, not new host settings. Providers activate only through enabled modules. Clients select application sessions with `X-Vestigo-Session-Id` or request `_meta["vestigo/sessionId"]`; IDs are 16–128 letters, digits, underscores or hyphens. Configure distinct bearer credentials and/or IDs for independent agents. No transport session switch, core resource store or implicit shared backend is enabled. See [provider configuration and identity](plugins.md#api-11-tool-providers).
