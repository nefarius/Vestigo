# MCP tools

The generated parameter catalog is [generated/mcp-tools.md](generated/mcp-tools.md). Examples below use the default loopback endpoint.

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

`stop_capture` cancels the reader and disposes the ETW session.

Capture duration cannot exceed `MaxCaptureDuration`. The spool is removed after `SpoolRetention` once the capture has stopped.

## Services

The `Services` module lists and controls Win32 services and kernel/file-system drivers. Names are service names, not display names. States are `stopped`, `start_pending`, `stop_pending`, `running`, `continue_pending`, `pause_pending` and `paused`. Start types are `boot`, `system`, `auto`, `demand` and `disabled`; `boot` and `system` are only valid for drivers.

`list_services` filters by `kind` (`win32`, `driver`, `all`), `nameContains` (name or display name) and `state`, sorted by name and capped by `limit` (default 200, maximum 1000; the result reports `Total` and `Truncated`). `get_service` adds the error control, exit codes and, while running, the process id. `start_service`, `stop_service` and `restart_service` wait for `running` or `stopped` for up to `timeoutSeconds` (maximum 300; 0 returns immediately on start and stop) and return a `timeout` error with the last state otherwise. `stop_service` with `stopDependents` stops running dependents first; without it a service with running dependents fails with `has_dependents`. Starting or stopping a service that is already in the target state succeeds without a call. `set_service_start_type` changes the start type through `ChangeServiceConfig`.

`create_driver_service` and `delete_service` use the bundled nefcon runner and only handle `KERNEL_DRIVER` services. `create_driver_service` needs an existing `.sys` file reachable through a root that allows `Execute` (`root:relative\path` or an absolute path inside such a root); `delete_service` takes a plain name and refuses Win32 and file-system driver services. A result with `RebootRequired` means the change completes after a restart.

Driver caveats: Windows only loads signed kernel drivers, so `start_service` on an unsigned driver fails with `driver_signature` (error 577) or `driver_blocked` (1275) unless test-signing mode is enabled (`bcdedit /set testsigning on`, then reboot) or the driver carries a valid signature. A driver that starts and immediately stops is reported as `timeout`; check `get_service` for the exit code. Mutating tools refuse the names in `Vestigo:Services:ProtectedNames`. They need the elevated service account; otherwise they return `access_denied`.

## Environment

`get_environment_variable` reads one variable from `machine` (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`), `user` (the registry hive of the user logged on at the console) or `process` scope. Registry values are returned unexpanded with their kind (`REG_SZ` or `REG_EXPAND_SZ`). User scope fails with `user_not_available` when nobody is logged on. `list_environment_variables` lists a scope, optionally names only.

`set_environment_variable` takes an `operation` of `set`, `unset`, `append` or `prepend`. Append and prepend treat the value as `;`-separated entries (for example `PATH`), skip entries that are already present, and keep an existing `REG_EXPAND_SZ` kind. `expand` writes `REG_EXPAND_SZ` for `set`. A change to machine or user scope broadcasts `WM_SETTINGCHANGE`; the result reports `broadcast` as `sent`, `timeout` or `skipped`. Names in `Vestigo:Environment:ProtectedNames` are refused with `protected_name`. The value is never echoed or logged.

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

Both endpoints use the same bearer authentication as `/mcp`.

`PUT /files/{root}/{path}?overwrite=false&deferToReboot=false` streams the request body to a temporary file in the target directory and commits it with an atomic replace. An optional `X-Content-SHA256` header is checked before the commit; a mismatch returns 422 and writes nothing. The response is JSON:

```json
{ "fullPath": "D:\\drops\\bin\\x.sys", "size": 11, "sha256": "...", "replacedExisting": false, "rebootRequired": false, "asideFile": null, "asideCleanup": null }
```

If the target is locked, for example a loaded driver or running executable, the old file is renamed to `<name>.vestigo-old-<guid>`, the new file takes its place, and the old one is deleted or scheduled for deletion at reboot (`asideCleanup`). With `deferToReboot=true` the new file instead becomes a pending replace through `MoveFileEx(DELAY_UNTIL_REBOOT | REPLACE_EXISTING)` and `rebootRequired` is `true`. If the file cannot be renamed either, the call fails with 423 and suggests `deferToReboot`. Status codes: 400 invalid path, 403 root lacks `Write`, 404 unknown root, 409 exists without `overwrite`, 413 over `MaxUploadBytes`, 422 hash mismatch.

`GET /files/{root}/{path}` streams a file from a root with `Read` access and supports `Range` requests.

## Processes

The `Processes` module is disabled by default and requires the `Jobs` module and at least one API token. These tools run arbitrary code by design; read [security.md](security.md#processes) first.

`run_process` starts a process as a job. `path` is a file under a root with `Execute` access (`name:relative\path` or an absolute path inside such a root), or a bare name such as `cmd.exe` resolved from the service `PATH` when `Vestigo:Processes:AllowPathLookup` is true (the default). `args` is an array; each element is quoted for `CommandLineToArgvW`. `workingDirectory` must be under a root with `Read` access and defaults to the executable's directory. `env` overrides environment variables. `.ps1` files run through `pwsh.exe` when it is installed, otherwise `powershell.exe`, with `-NoProfile -ExecutionPolicy Bypass -File`.

`context` is `service` (default, the service account, normally SYSTEM) or `interactive` (the active console user on `winsta0\default`). `elevated: true` with `interactive` uses the user's linked administrator token; it returns `elevation_unavailable` for a standard user. When nobody is logged on at the console, interactive requests fail up front with `no_interactive_session` and no job is created. stdout and stderr are appended to the job output and stdin is NUL. Console programs write in the OEM code page, so non-ASCII output may be garbled.

`wait` is seconds (0 to 300). With `wait` greater than 0 the call waits that long and returns the job state plus the last 16 KB of output; `StillRunning` tells you whether the job outlived the wait. With `wait` 0 it returns the job id immediately; use `wait_job`, `read_job_output` and `cancel_job`. `cancel_job` kills the whole process tree. Interactive processes run in a job object with kill-on-close, and the job tree is ended when their output passes `MaxJobOutputBytes`, so descendants also end when the main process exits.

`list_processes` returns pid, name, session id and start time, filtered by `nameFilter` (substring), `pid` or `sessionId`.

`close_app` posts `WM_CLOSE` to visible top-level windows selected by `pid`, `processName` and/or `titleRegex` (pid or name, narrowed by the title pattern). Windows belong to a desktop, so the default `interactive` context starts `Vestigo.exe --session-helper close-window ...` in the console user's session; it prints JSON and exits. After `gracefulTimeoutSeconds` the result lists the processes that are still alive (`StillRunning`); Vestigo never kills them. Use `elevated: true` to close elevated windows. `context: service` only sees the service's own desktop.

`kill_process` terminates by `pid` or exact `processName` (every match). `entireTree` also kills descendants. It refuses its own pid, pid 0 and pid 4.

## Remote debugger

The `RemoteDebugger` module is disabled by default and requires the `Jobs` and `Processes` modules (and therefore at least one API token). It manages the Visual Studio Remote Debugging Tools (`msvsmon.exe`) for Visual Studio 2022 (17) and 2026 (18). A running monitor lets a remote debugger run code on this machine; read [security.md](security.md#remote-debugger) first.

Workflow: `get_remote_debugger_status` (is it installed?) -> `install_remote_debugger` (job) -> optionally `prepare_remote_debugger_computer` (firewall) -> `start_remote_debugger` -> connect Visual Studio (Debug > Attach to Process, connection target `host:4026`) -> `stop_remote_debugger`.

Every tool takes `version` (`2022` or `2026`) and `arch` (`x64` default, `arm64`, `x86`).

`get_remote_debugger_status` lists every version and architecture with the discovered `msvsmon.exe` path, how it was found (`standalone`, `ide`, `vswhere`, `registry`, `extra`), its file version and the installer URL, plus each running `msvsmon.exe` with pid, session id, port, whether that port is listening and whether Vestigo started it. The port comes from the process command line (read through CIM) and falls back to the port Vestigo used or the default.

`install_remote_debugger` downloads `https://aka.ms/vs/<17|18>/release/RemoteTools.<amd64|arm64|x86>ret.enu.exe` to a temporary directory under the spool, requires a valid Authenticode signature (`WinVerifyTrust`) whose signer CN or O is `Microsoft Corporation`, and runs it as the service account with `/install /quiet /norestart`. Everything happens inside a job so the call returns a job id at once; the temporary files are removed at the end. It returns `Skipped` when `msvsmon.exe` is already found, unless `force` is true. `Vestigo:RemoteDebugger:InstallerDownloadTimeout` limits the download.

`start_remote_debugger` runs `msvsmon.exe /nostatus /silent /nofirewallwarn /port <port> /timeout <seconds>` in the `interactive` context by default (`elevated` uses the user's linked administrator token). `port` defaults to `Vestigo:RemoteDebugger:DefaultPort` (4026); `timeoutSeconds` (30 to 86400, default 3600) is how long the monitor waits without a connection. `auth` is `windows` (default; `allowUsers` entries become `/allow`) or `none` (`/noauth /anyuser`), which needs `iUnderstandNoAuthIsInsecure: true` and adds a warning to the result and the log. A second instance on a port that is in use is refused with `port_in_use`. The result has the pid, port, the sanitised arguments, whether the port is listening, and the job id.

`stop_remote_debugger` takes a `pid` of a running `msvsmon.exe` or `all: true` for the instances Vestigo started. It posts a close request, waits `gracefulTimeoutSeconds`, then kills the process tree.

`prepare_remote_debugger_computer` runs `msvsmon.exe /prepcomputer /private /quiet` as a job (default `service` context) to configure firewall rules and permissions for private networks.

## Devices

The `Devices` module changes drivers and device state. Everything here needs the elevated service account and drivers run in the kernel: installing one is arbitrary code execution at the highest privilege. That is the purpose of the module, so enable it only on machines you intend to debug drivers on (`Vestigo:Modules:Devices:Enabled`), and protect `/mcp` with an API token.

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

Class GUIDs may be written with or without braces and must not be empty. Instance IDs must contain a `\`, and no value may be empty, start with `-`, or contain control characters. Device state tools map library failures to stable error codes: `device_not_found`, `config_manager_failed`, `win32_failed`, `windows_required` and `device_operation_failed`.

The query tools are what Nefarius.Utilities.DeviceManagement 6.1.0 supports today. Enumerating all devices and listing the driver store need a newer upstream release and will be added in a later change.

### Typical driver development workflows

Bring up a root-enumerated driver:

1. `create_device_node` with the class name, class GUID and a hardware ID such as `Root\MyDriver`. (`install_driver` with a `hardwareId` does this and the install in one step.)
2. `install_driver` with the INF root path and the same `hardwareId`.
3. If the driver needs a filter on a class: `install_filter_driver`, or `add_class_filter` when the service already exists.
4. `find_devices_by_hardware_id` to get the instance ID, then `restart_device` so the stack is rebuilt with the new driver or filter. Do a reboot instead when `rebootRequired` is true.

Iterate on a new build: `install_driver` again with the same hardware ID and `noDuplicates`, then `restart_device`.

Tear down: `uninstall_filter_driver` (with `infPath` to purge its package) or `remove_class_filter`, then `uninstall_driver`, `remove_device_node`, and finally `remove_driver_store_package` with the INF name so old packages do not accumulate in the driver store.
