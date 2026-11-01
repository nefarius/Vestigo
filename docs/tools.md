# MCP tools

Point an MCP client at `http://127.0.0.1:5098/mcp` (Streamable HTTP). When `ApiTokens` is set, send `Authorization: Bearer <token>`. The generated parameter catalog is [generated/mcp-tools.md](generated/mcp-tools.md).

## Offline

`inspect_wpp_pdb` returns WPP control GUIDs, names, and flag bits from one PDB.

`validate_decoding_sources` loads explicit PDB and TMF paths and reports whether a decoding context was built.

`scan_etl_metadata` returns the distinct PDB name, GUID, age, and symbol-server index prefix referenced by the ETL files.

`convert_etl` writes `json` or `ndjson` under an allowed output root. The tool result is a summary: path, event count, bytes, missing WPP formats, truncation, and symbol diagnostics. It does not return the trace payload.

## Realtime

`list_etw_sessions` returns every session name Windows reports. It does not stop sessions.

`start_capture` accepts provider GUIDs and/or PDB paths. PDB control GUIDs are enabled automatically. TMF files contribute message formats but not provider GUIDs. The call returns when the session has started, not when the trace ends. Keyword bitmasks accept a decimal value or a `0x` hexadecimal value. The level must be a named `TraceLevelSelection` value.

`list_captures` and `get_capture` report state, event count, bytes, missing formats, and the stop reason.

`read_capture` returns NDJSON lines after `afterSequence`. Pass the returned `NextSequence` to the next call. `limit` is clamped to `MaxReadBatch`.

To follow a capture live, loop on `read_capture` with `waitSeconds` greater than zero (clamped to `MaxReadWait`). The call returns as soon as new events are spooled, or empty at the timeout, and stops waiting when the capture ends. Each page carries `State` and `Completed`; stop looping when `Completed` is true.

`attach_etw_session` attaches read-only to an existing realtime session that is not owned by Vestigo, such as one started by `tracelog`, and decodes its events with optional PDB or TMF paths. The session name must match `Vestigo:AttachableSessions` (empty by default, which disables attach). The job appears in `list_captures` with `Mode` `Attached` and is read with `read_capture`. `stop_capture` only detaches; the session itself is never stopped, and it is never recorded as a Vestigo-owned session.

`stop_capture` cancels the reader and disposes the ETW session.

Capture duration cannot exceed `MaxCaptureDuration`. The spool is removed after `SpoolRetention` once the capture has stopped.

## Debug output

The `DebugOutput` module is disabled by default. It runs the bundled DebugViewConsole (DebugView++ 1.30.0.0) and forwards Win32 `OutputDebugString` messages to the calling agent. Only one capture runs at a time; a second `start_debug_capture` fails with `capture_busy`. Read [security.md](security.md#debug-output) first.

`start_debug_capture` starts DebugViewConsole with capture-time substring filters (`includeMessages`, `excludeMessages`, `includeProcesses`, `excludeProcesses`) and an optional `duration` capped by `Vestigo:DebugOutput:MaxDuration`. The call returns when the process has been launched, not when capture ends. A successful launch may already be `Running` or `Failed`; that status does not guarantee the capture is ready. If another DebugView instance owns `DBWIN_BUFFER`, the capture ultimately records `Failed` with `dbwin_in_use`.

`list_debug_captures` and `get_debug_capture` report state, message count, bytes, the child PID, and the stop reason.

`read_debug_output` returns NDJSON lines after `afterSequence`. Each line is `{receivedAt, time, pid, processName, message}`. Pass the returned `NextSequence` to the next call. `limit` is clamped to `MaxReadBatch`. To follow a capture live, loop with `waitSeconds` greater than zero (clamped to `MaxReadWait`) and stop when `Completed` is true. Optional read-time filters (`messageRegex`, `excludeRegex`, `processIds`, `processNames` globs) skip non-matching lines; `nextSequence` still advances past every scanned line. Regexes use `RegexOptions.NonBacktracking | IgnoreCase`.

`stop_debug_capture` sends Ctrl+C to DebugViewConsole and then kills the process tree.

Windows allows only one `DBWIN_BUFFER` reader per session. Another DebugView or DebugView++ instance causes the capture to fail with `dbwin_in_use` after launch. Processes that have a debugger attached send their output to the debugger instead of this capture. Kernel `DbgPrint` is not captured.

## Services

The `Services` module lists and controls Win32 services and kernel/file-system drivers. Names are service names, not display names. States are `stopped`, `start_pending`, `stop_pending`, `running`, `continue_pending`, `pause_pending` and `paused`. Start types are `boot`, `system`, `auto`, `demand` and `disabled`; `boot` and `system` are only valid for drivers.

`list_services` filters by `kind` (`win32`, `driver`, `all`), `nameContains` (name or display name) and `state`, sorted by name and capped by `limit` (default 200, maximum 1000; the result reports `Total` and `Truncated`). `get_service` adds the error control, exit codes and, while running, the process id. `start_service`, `stop_service` and `restart_service` wait for `running` or `stopped` for up to `timeoutSeconds` (maximum 300; 0 returns immediately on start and stop) and return a `timeout` error with the last state otherwise. `stop_service` with `stopDependents` stops running dependents first; without it a service with running dependents fails with `has_dependents`. Starting or stopping a service that is already in the target state succeeds without a call. `set_service_start_type` changes the start type through `ChangeServiceConfig`.

`create_driver_service` and `delete_service` use the bundled nefcon runner and only handle `KERNEL_DRIVER` services. `create_driver_service` needs an existing `.sys` file reachable through a root that allows `Execute` (`root:relative\path` or an absolute path inside such a root); `delete_service` takes a plain name and refuses Win32 and file-system driver services. A result with `RebootRequired` means the change completes after a restart.

Driver caveats: Windows only loads signed kernel drivers, so `start_service` on an unsigned driver fails with `driver_signature` (error 577) or `driver_blocked` (1275) unless test-signing mode is enabled or the driver carries a valid signature. Use `get_boot_security` to see the BCD value and the running kernel state; `set_test_signing` writes the default boot entry when `Vestigo:System:AllowBootConfigurationChanges` is true, then reboot. Secure Boot blocks test signing. A driver that starts and immediately stops is reported as `timeout`; check `get_service` for the exit code. Mutating tools refuse the names in `Vestigo:Services:ProtectedNames`. They need the elevated service account; otherwise they return `access_denied`.

## Software

The `Software` module inventories installed programs from the `Uninstall` registry keys: `HKLM` in the 64-bit and 32-bit views plus every loaded user hive. Entries without a display name are skipped. Each entry has an `Id` such as `machine-x64:{KeyName}`, `machine-x86:{KeyName}` or `user:<SID>:{KeyName}`, and reports name, version, publisher, install date, install location and source, `UninstallString`, `QuietUninstallString`, size, scope, architecture, MSI product code and the detected installer type (`msi`, `burn`, `inno`, `nsis`, `installshield`, `unknown`).

`list_installed_software` filters by `nameContains`, `publisherContains` and `scope` (`machine`, `user`, `all`); system components and updates (entries with a parent) are hidden unless `includeSystemComponents` or `includeUpdates` is set. Results are sorted by name and capped by `limit` (default 200, maximum 1000). `get_installed_software` adds the silent command Vestigo would run, or why none exists.

`uninstall_software` runs a silent uninstall as a job (see [Jobs](#jobs)). It is refused with `uninstall_disabled` unless `Vestigo:Software:AllowUninstall` is `true`. MSI products always run `msiexec.exe /x {ProductCode} /qn /norestart /l*v <log>` (the stored string is often `/I`). Other entries use `QuietUninstallString` when present, otherwise known switches are appended for Burn (`/uninstall /quiet /norestart`), Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) and NSIS (`/S`); other installers return `unsupported_installer`. Uninstallers in locations that non-administrators can modify are refused with `untrusted_executable`. `dryRun` returns the plan only; `wait` (0 to 300 seconds) waits and returns the exit code, `RebootRequired` (exit 3010 or 1641 count as success), whether the entry is `StillInstalled`, and the output tail including the MSI log. A run is killed after `Vestigo:Software:MaxUninstallDuration`. Entries that are `NoRemove`, system components, per-user, or match `Vestigo:Software:ProtectedNames` (default `Vestigo`, matched against the display name) are refused.

## Environment

`get_environment_variable` reads one variable from `machine` (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`), `user` (the registry hive of the user logged on at the console) or `process` scope. Registry values are returned unexpanded with their kind (`REG_SZ` or `REG_EXPAND_SZ`). User scope fails with `user_not_available` when nobody is logged on. `list_environment_variables` lists a scope, optionally names only.

`set_environment_variable` takes an `operation` of `set`, `unset`, `append` or `prepend`. Append and prepend treat the value as `;`-separated entries (for example `PATH`), skip entries that are already present, and keep an existing `REG_EXPAND_SZ` kind. `expand` writes `REG_EXPAND_SZ` for `set`. A change to machine or user scope broadcasts `WM_SETTINGCHANGE`; the result reports `broadcast` as `sent`, `timeout` or `skipped`. Names in `Vestigo:Environment:ProtectedNames` are refused with `protected_name`. The value is never echoed or logged.

## System

The `System` module reports the host Windows version and boot-security posture through `Nefarius.Utilities.WindowsVersion`. It is enabled by default. Writes stay off until `Vestigo:System:AllowBootConfigurationChanges` is `true`.

`get_system_info` returns the OS name, edition, display version (for example 24H2), version string, major/minor/build/UBR, whether it is Windows 11 or Server, whether the install was in-place upgraded (`grandfathered`), OS and processor bitness, ARM64, whether UAC is disabled, and the machine name. A probe that fails becomes a null field instead of failing the call.

`get_boot_security` returns `uefi`, `secureBoot`, `testSigningConfigured` (BCD on the default boot entry), `testSigningActive` (the running kernel), `whqlDeveloperTestMode`, and `rebootPending` when the BCD value and the running state differ. `notes` explain Secure Boot blocking test signing, a pending reboot, and that BCD changes apply only to the default boot entry. WHQL developer test mode is not present on every Windows build; a missing value is null.

`set_test_signing` and `set_whql_developer_test_mode` fail with `boot_changes_disabled` unless the option is enabled. Turning test signing on fails with `secure_boot_enabled` unless Secure Boot is known to be disabled. A BCD write failure is `bcd_write_failed`; a WHQL policy write failure is `ci_write_failed`; lack of elevation is `access_denied`. Vestigo never reboots the machine: BCD test-signing takes effect at the next boot, and WHQL developer test mode availability depends on the Windows build.

## Logs

`tail_text_file` returns the last `lines` lines of a file under a root with `Read` access, examining at most `maxBytes` from the end (both clamped to `Vestigo:Logs`). The file is opened with shared access. UTF-8 and UTF-16 byte order marks are detected; otherwise UTF-8 is assumed. The result has `lines`, a `cursor` (`offset`, `length`, `identity` = creation time ticks), `truncated` and `encoding`.

`read_text_file_since` continues from `offset` and `identity` of a previous cursor and returns complete lines only; an unterminated last line waits until it is finished. If the file is shorter than the offset or its identity changed, reading restarts at the beginning and `rotated` is true. Page with `maxLines` while `hasMore` is true. A single line longer than the byte cap is returned in pieces with `truncated` set.

## Event log

`list_event_logs` lists channels with record count and enabled state, optionally filtered by `nameContains`.

`query_event_log` reads a channel (`Application`, `System`, ...) or an `.evtx` file under a root with `Read` access. Filters `providers`, `levels` (`critical`, `error`, `warning`, `information`, `verbose`), `eventIds`, `since` and `until` (ISO 8601) are turned into an XPath that is returned as `query`; alternatively pass a raw `xpath` (not combined with filters). `messageContains` filters the rendered message after reading and is bounded by `Vestigo:EventLog:MaxScannedEvents` (`scanLimitReached`). Results are newest first by default and capped by `Vestigo:EventLog:MaxEvents`; `truncated` says more matched. Messages are null when the publisher is not installed. `includeXml` adds the raw event XML.

## Jobs

`list_jobs`, `get_job`, `wait_job`, `read_job_output` and `cancel_job` manage background jobs that other tools start. `read_job_output` returns text after a byte `offset`; pass the returned `NextOffset` to continue. Finished jobs are kept for `JobRetention`.

## Files

Paths are `root:relative\path` (or an absolute path inside a root). Reads need `Read`, everything that changes files needs `Write`. A root, or a directory containing a root, can never be deleted or moved, and NTFS stream syntax (`file:stream`) is rejected.

`list_roots` returns each root with its access flags and free space. `list_directory` lists a directory (`maxEntries` up to 5000, `Truncated` tells you more exist). `stat_path` returns size and times, an optional SHA-256, and for `.exe`, `.dll` and `.sys` files the version resource plus the embedded Authenticode status from `WinVerifyTrust` (`Signed`, `Unsigned` or `Invalid`). Catalog-signed files, such as many Windows binaries, have no embedded signature and report `Unsigned`.

`copy_path`, `move_path`, `delete_path` (non-empty directories need `recursive`) and `make_directory` do what they say. Directory copies skip links.

`fetch_file` downloads an HTTP(S) URL or copies an ordinary UNC file (not `\\?\` or `\\.\` device paths) into a root as a background job; use `wait_job` and `read_job_output`. Pass `expectedSha256` to reject a wrong file, and `overwrite` to replace an existing one. The size is capped by `Vestigo:Files:MaxUploadBytes`.

`get_upload_url` returns the `PUT` URL for a root-relative path plus ready-to-run `curl.exe` and `Invoke-WebRequest` commands. `delete_file_on_reboot` schedules a deletion with `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`; the service must run elevated.

### HTTP transfer endpoints

Both endpoints use the same authentication as `/mcp`: bearer tokens when `ApiTokens` is set, otherwise anonymous.

`PUT /files/{root}/{path}?overwrite=false&deferToReboot=false` streams the request body to a temporary file in the target directory and commits it with an atomic replace. An optional `X-Content-SHA256` header is checked before the commit; a mismatch returns 422 and writes nothing. The response is JSON:

```json
{ "fullPath": "D:\\drops\\bin\\x.sys", "size": 11, "sha256": "...", "replacedExisting": false, "rebootRequired": false, "asideFile": null, "asideCleanup": null }
```

If the target is locked, for example a loaded driver or running executable, the old file is renamed to `<name>.vestigo-old-<guid>`, the new file takes its place, and the old one is deleted or scheduled for deletion at reboot (`asideCleanup`). With `deferToReboot=true` the new file instead becomes a pending replace through `MoveFileEx(DELAY_UNTIL_REBOOT | REPLACE_EXISTING)` and `rebootRequired` is `true`. If the file cannot be renamed either, the call fails with 423 and suggests `deferToReboot`. Status codes: 400 invalid path, 403 root lacks `Write`, 404 unknown root, 409 exists without `overwrite`, 413 over `MaxUploadBytes`, 422 hash mismatch.

`GET /files/{root}/{path}` streams a file from a root with `Read` access and supports `Range` requests.

## Processes

The `Processes` module is disabled by default and requires the `Jobs` module. These tools run arbitrary code by design; read [security.md](security.md#processes) first. Enabling it without `ApiTokens` is allowed and logs a startup `SECURITY WARNING`.

`run_process` starts a process as a job. `path` is a file under a root with `Execute` access (`name:relative\path` or an absolute path inside such a root), or a bare name such as `cmd.exe` resolved from the service `PATH` when `Vestigo:Processes:AllowPathLookup` is true (the default). `args` is an array; each element is quoted for `CommandLineToArgvW`. `workingDirectory` must be under a root with `Read` access and defaults to the executable's directory. `env` overrides environment variables. `.ps1` files run through `pwsh.exe` when it is installed, otherwise `powershell.exe`, with `-NoProfile -ExecutionPolicy Bypass -File`.

`context` is `service` (default, the service account, normally SYSTEM) or `interactive` (the active console user on `winsta0\default`). `elevated: true` with `interactive` uses the user's linked administrator token; it returns `elevation_unavailable` for a standard user. When nobody is logged on at the console, interactive requests fail up front with `no_interactive_session` and no job is created. stdout and stderr are appended to the job output and stdin is NUL. Console programs write in the OEM code page, so non-ASCII output may be garbled.

`wait` is seconds (0 to 300). With `wait` greater than 0 the call waits that long and returns the job state plus the last 16 KB of output; `StillRunning` tells you whether the job outlived the wait. With `wait` 0 it returns the job id immediately; use `wait_job`, `read_job_output` and `cancel_job`. `cancel_job` kills the whole process tree. Interactive processes run in a job object with kill-on-close, and the job tree is ended when their output passes `MaxJobOutputBytes`, so descendants also end when the main process exits.

`list_processes` returns pid, name, session id and start time, filtered by `nameFilter` (substring), `pid` or `sessionId`.

`close_app` posts `WM_CLOSE` to visible top-level windows selected by `pid`, `processName` and/or `titleRegex` (pid or name, narrowed by the title pattern). Windows belong to a desktop, so the default `interactive` context starts `Vestigo.exe --session-helper close-window ...` in the console user's session; it prints JSON and exits. After `gracefulTimeoutSeconds` the result lists the processes that are still alive (`StillRunning`); Vestigo never kills them. Use `elevated: true` to close elevated windows. `context: service` only sees the service's own desktop.

`kill_process` terminates by `pid` or exact `processName` (every match). `entireTree` also kills descendants. It refuses its own pid, pid 0 and pid 4.

## Desktop

The `Desktop` module is disabled by default. It lists Win32 windows and the GDI/physical displays they are rendered on. Window titles from the console user's desktop are information disclosure; read [security.md](security.md#desktop) first. Enabling it without `ApiTokens` is allowed and logs a startup `SECURITY WARNING`.

`list_windows` returns each matching window's handle (hex), title, class, owning process (pid, name, path when `PROCESS_QUERY_LIMITED_INFORMATION` is allowed), parent (null when the parent is the desktop), owner, root owner, visibility, minimized/maximized/cloaked, `GetWindowRect` bounds, DWM extended-frame bounds, client size, window DPI, nearest GDI display (`\\.\DISPLAY1`), and every display its bounds intersect. Coordinates are physical pixels: the enumerator switches the thread to per-monitor DPI awareness for the call.

Filters combine: `pid` or `processName`, narrowed by `titleRegex` and `className`. `includeHidden` adds invisible windows; cloaked windows (for example an app on another virtual desktop) still appear when they are visible to `IsWindowVisible`. `includeChildren` also walks `EnumChildWindows`; child-window titles are omitted so control text is not returned or matched. `limit` is 1 to 5000 (default 500); `truncated` is true when the cap was hit. A `titleRegex` that exceeds its one-second match limit fails with `invalid_request`.

Windows belong to a desktop, so the default `interactive` context starts `Vestigo.exe --session-helper list-windows` in the console user's session; it prints JSON and exits. `context: service` only sees the service's own desktop (session 0 when Vestigo runs as a service). Interactive enumeration needs a console user; it fails with `no_interactive_session` otherwise. Secure and locked desktops are not visible. `ProcessPath` may be null for protected processes. Titles are read with `SendMessageTimeout` so a hung window cannot stall the listing (that title is then empty).

`list_displays` returns the GDI topology (`\\.\DISPLAYn`, bounds, work area, DPI, primary) plus the physical targets `QueryDisplayConfig` reports for each source, including cloned outputs (`friendlyName`, `devicePath`, EDID manufacturer/product, output technology, connector instance). When that API is unavailable (session 0, some RDP sessions) the tool falls back to `EnumDisplayDevices` and may return empty monitor details.

## Audio

The `Audio` module is disabled by default. It lists Core Audio playback (`render`) and recording (`capture`) endpoints and can switch the default device. Endpoint names and the processes using them are information disclosure; changing the default device is a separate gate. Read [security.md](security.md#audio) first. Enabling the module without `ApiTokens` is allowed and logs a startup `SECURITY WARNING`.

`list_audio_endpoints` returns each matching endpoint's id, friendly name, device description, interface name, flow, state, form factor, the default roles it currently holds (`console`, `multimedia`, `communications`), master volume percent, mute, per-channel volumes, a short peak-meter sample (`PeakPercent`), whether playback or recording looks active, and the audio sessions on that endpoint (`processId`, `processName`, `displayName`, session state, `isSystemSounds`). `Active` is true when any session is in the Core Audio active state or the sampled peak is above zero. Volume, mute, peak, and sessions are omitted (null or empty) when the endpoint does not expose them.

Filters: `flow` is `all` (default), `render`, or `capture`. `state` is `active` (default) or `all` (includes disabled, unplugged, and not-present). `id` returns one endpoint. `includeSessions` defaults to true. Peak sampling lasts `Vestigo:Audio:MeterSampleMilliseconds` (0 disables it). Audio sessions belong to a logon session, so the default `interactive` context starts `Vestigo.exe --session-helper list-audio` in the console user's session. `context: service` enumerates in-process and does not see user-session playback or recording.

`set_default_audio_endpoint` sets the default for the endpoint's flow. `role` `default` is Windows' Default Device (console and multimedia). `communications` is the Default Communication Device. `all` sets both. It is refused with `set_default_disabled` unless `Vestigo:Audio:AllowSetDefault` is `true`. The endpoint must exist and be active (`not_found`, `not_active`). The result includes the applied roles and a refreshed listing of that flow so the new `defaultFor` flags can be confirmed. Switching uses the undocumented `IPolicyConfig` interface and can fail with `set_default_failed` if that contract is unavailable. Per-app device overrides from Settings are neither shown nor changed.

## Remote debugger

The `RemoteDebugger` module is disabled by default and requires the `Jobs` and `Processes` modules. Enabling those without `ApiTokens` logs a startup `SECURITY WARNING`. It manages the Visual Studio Remote Debugging Tools (`msvsmon.exe`) for Visual Studio 2022 (17) and 2026 (18). A running monitor lets a remote debugger run code on this machine; read [security.md](security.md#remote-debugger) first.

Workflow: `get_remote_debugger_status` (is it installed?) -> `install_remote_debugger` (job) -> optionally `prepare_remote_debugger_computer` (firewall) -> `start_remote_debugger` -> connect Visual Studio (Debug > Attach to Process, connection target `host:4026`) -> `stop_remote_debugger`.

Every tool takes `version` (`2022` or `2026`) and `arch` (`x64` default, `arm64`, `x86`).

`get_remote_debugger_status` lists every version and architecture with the discovered `msvsmon.exe` path, how it was found (`standalone`, `ide`, `vswhere`, `registry`, `extra`), its file version and the installer URL, plus each running `msvsmon.exe` with pid, session id, port, whether that port is listening and whether Vestigo started it. The port comes from the process command line (read through CIM) and falls back to the port Vestigo used or the default.

`install_remote_debugger` downloads `https://aka.ms/vs/<17|18>/release/RemoteTools.<amd64|arm64|x86>ret.enu.exe` to a temporary directory under the spool, requires a valid Authenticode signature (`WinVerifyTrust`) whose signer CN or O is `Microsoft Corporation`, and runs it as the service account with `/install /quiet /norestart`. Everything happens inside a job so the call returns a job id at once; the temporary files are removed at the end. It returns `Skipped` when `msvsmon.exe` is already found, unless `force` is true. `Vestigo:RemoteDebugger:InstallerDownloadTimeout` limits the download.

`start_remote_debugger` runs `msvsmon.exe /nostatus /silent /nofirewallwarn /port <port> /timeout <seconds>` in the `interactive` context by default (`elevated` uses the user's linked administrator token). `port` defaults to `Vestigo:RemoteDebugger:DefaultPort` (4026); `timeoutSeconds` (30 to 86400, default 3600) is how long the monitor waits without a connection. `auth` is `windows` (default; `allowUsers` entries become `/allow`) or `none` (`/noauth /anyuser`), which needs `iUnderstandNoAuthIsInsecure: true` and adds a warning to the result and the log. A second instance on a port that is in use is refused with `port_in_use`. The result has the pid, port, the sanitised arguments, whether the port is listening, and the job id.

`stop_remote_debugger` takes a `pid` of a running `msvsmon.exe` or `all: true` for the instances Vestigo started. It posts a close request, waits `gracefulTimeoutSeconds`, then kills the process tree.

`prepare_remote_debugger_computer` runs `msvsmon.exe /prepcomputer /private /quiet` as a job (default `service` context) to configure firewall rules and permissions for private networks.

## Devices

The `Devices` module changes drivers and device state. Mutating operations need the elevated service account; the query tools are read-only and mostly work without elevation. Drivers run in the kernel: installing one is arbitrary code execution at the highest privilege. That is the purpose of the module, so enable it only on machines you intend to debug drivers on (`Vestigo:Modules:Devices:Enabled`), and protect `/mcp` with an API token whenever more than one client can reach it.

Driver actions go through the bundled nefcon CLI with fixed arguments (see `INefconRunner`). Every result has `exitCode`, `outputTail` and `rebootRequired`, plus `timedOut`. Exit code `0` is success and `3010` is success that needs a restart (`rebootRequired: true`). Any other exit code returns `success: false` with error code `nefcon_failed` and the same result in `value`; exit code `1` normally means the service is not elevated or nefcon rejected an argument. A run longer than `Vestigo:Devices:NefconTimeout` is killed (`nefcon_timeout`). Calls run one at a time. A reboot is never triggered by Vestigo; apply it yourself when `rebootRequired` is true.

INF paths (`install_driver`, `uninstall_driver`, `install_filter_driver`, `uninstall_filter_driver`, `inf_default_install`, `inf_default_uninstall`) must be existing `.inf` files inside a root with `Read` access, as `name:relative\path` or an absolute path. The driver's other files are read by Windows from beside the INF, so give the root the whole driver package directory.

| Tool | Purpose |
| --- | --- |
| `create_device_node` | Create a ROOT device node (`className`, `classGuid`, `hardwareId`, optional `noDuplicates`). |
| `remove_device_node` | Remove a node by `hardwareId` and `classGuid`. |
| `install_driver` | Without `hardwareId`: install the package. With it: create the ROOT node and install the driver on it; `noDuplicates` and `removeDuplicates` (which needs `noDuplicates`) only apply with a hardware ID. |
| `uninstall_driver` | Uninstall the driver described by an INF. |
| `install_filter_driver` | Install a filter driver INF and register its class filter. |
| `uninstall_filter_driver` | Remove the class filter and service (`classGuid`, `serviceName`, `position` upper or lower). With `infPath` the driver store package is purged too; `allVersions` (needs `infPath`) purges every version. |
| `add_class_filter`, `remove_class_filter` | Edit a class's upper or lower filter list for an existing service. |
| `inf_default_install`, `inf_default_uninstall` | Run `[DefaultInstall]` or `[DefaultUninstall]` of an INF. |
| `remove_driver_store_package` | Remove driver store packages by original INF name (for example `mydriver.inf`), optionally narrowed by `classGuid` and `serviceName`. nefcon has no all-versions switch for this command, so it removes what the name and filters match. |
| `restart_device`, `enable_device`, `disable_device` | Change state by instance ID (for example `ROOT\SYSTEM\0001`). |
| `find_devices_by_interface` | Instance IDs and device paths exposing an interface GUID (at most 256; `truncated` tells if the cap was hit; `presentOnly` false includes phantom devices). |
| `find_devices_by_hardware_id` | Instance IDs in a device class whose hardware IDs match (`presentOnly`, `allowPartial`). |
| `find_devices` | Enumerate device nodes with optional AND filters `nameContains` (description substring), `hardwareId` (exact, or prefix with a trailing `*`), `deviceClass` (class name or GUID), `presentOnly` (default true) and `limit` (default 100, max 500). Returns `devices`, `total` and `truncated`. |
| `get_device` | Details of one device by `instanceId`: hardware and compatible IDs, class, service, present flag, `problemCode` (0 is healthy), `devNodeStatus` and driver key, description, provider, version, date and INF. Properties a device lacks are null. |
| `get_class_filters` | Upper and lower filter driver service names registered for a `classGuid` (registry read, no elevation needed). |
| `list_driver_store` | Driver store packages with `nameContains` (published or original INF), `provider`, `deviceClass` (name or GUID), `includeInbox` (default false) and `limit` (default 100, max 500); each has published name, original INF, provider, class, version, date, architecture and bound device instance IDs. |

Class GUIDs may be written with or without braces and must not be empty. Instance IDs must contain a `\`, and no value may be empty, start with `-`, or contain control characters. Device state tools map library failures to stable error codes: `device_not_found`, `config_manager_failed`, `win32_failed`, `windows_required` and `device_operation_failed`.

The query tools (`find_devices`, `get_device`, `get_class_filters`, `list_driver_store`) run in-process through Nefarius.Utilities.DeviceManagement 6.2.0 and never start nefcon. They are read-only and mostly work without elevation, though some device properties or driver store details may be missing for an unprivileged service account.

### Typical driver development workflows

Bring up a root-enumerated driver:

1. `create_device_node` with the class name, class GUID and a hardware ID such as `Root\MyDriver`. (`install_driver` with a `hardwareId` does this and the install in one step.)
2. `install_driver` with the INF root path and the same `hardwareId`.
3. If the driver needs a filter on a class: `install_filter_driver`, or `add_class_filter` when the service already exists.
4. `find_devices_by_hardware_id` to get the instance ID, then `restart_device` so the stack is rebuilt with the new driver or filter. Do a reboot instead when `rebootRequired` is true.

Iterate on a new build: `install_driver` again with the same hardware ID and `noDuplicates`, then `restart_device`.

Tear down: `uninstall_filter_driver` (with `infPath` to purge its package) or `remove_class_filter`, then `uninstall_driver`, `remove_device_node`, and finally `remove_driver_store_package` with the INF name so old packages do not accumulate in the driver store.
