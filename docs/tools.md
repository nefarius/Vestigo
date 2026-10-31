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

## Jobs

`list_jobs`, `get_job`, `wait_job`, `read_job_output` and `cancel_job` manage background jobs that other tools start. `read_job_output` returns text after a byte `offset`; pass the returned `NextOffset` to continue. Finished jobs are kept for `JobRetention`.

## Processes

The `Processes` module requires the `Jobs` module. These tools run arbitrary code by design; read [security.md](security.md#processes) first.

`run_process` starts a process as a job. `path` is a file under a root with `Execute` access (`name:relative\path` or an absolute path inside such a root), or a bare name such as `cmd.exe` resolved from the service `PATH` when `Vestigo:Processes:AllowPathLookup` is true (the default). `args` is an array; each element is quoted for `CommandLineToArgvW`. `workingDirectory` must be under a root with `Read` access and defaults to the executable's directory. `env` overrides environment variables. `.ps1` files run through `pwsh.exe` when it is installed, otherwise `powershell.exe`, with `-NoProfile -ExecutionPolicy Bypass -File`.

`context` is `service` (default, the service account, normally SYSTEM) or `interactive` (the active console user on `winsta0\default`). `elevated: true` with `interactive` uses the user's linked administrator token; it returns `elevation_unavailable` for a standard user. When nobody is logged on at the console, interactive requests fail up front with `no_interactive_session` and no job is created. stdout and stderr are appended to the job output and stdin is NUL. Console programs write in the OEM code page, so non-ASCII output may be garbled.

`wait` is seconds (0 to 300). With `wait` greater than 0 the call waits that long and returns the job state plus the last 16 KB of output; `StillRunning` tells you whether the job outlived the wait. With `wait` 0 it returns the job id immediately; use `wait_job`, `read_job_output` and `cancel_job`. `cancel_job` kills the whole process tree. Interactive processes run in a job object with kill-on-close, so descendants also end when the main process exits.

`list_processes` returns pid, name, session id and start time, filtered by `nameFilter` (substring), `pid` or `sessionId`.

`close_app` posts `WM_CLOSE` to visible top-level windows selected by `pid`, `processName` and/or `titleRegex` (pid or name, narrowed by the title pattern). Windows belong to a desktop, so the default `interactive` context starts `Vestigo.exe --session-helper close-window ...` in the console user's session; it prints JSON and exits. After `gracefulTimeoutSeconds` the result lists the processes that are still alive (`StillRunning`); Vestigo never kills them. Use `elevated: true` to close elevated windows. `context: service` only sees the service's own desktop.

`kill_process` terminates by `pid` or exact `processName` (every match). `entireTree` also kills descendants. It refuses its own pid, pid 0 and pid 4.

## Remote debugger

The `RemoteDebugger` module requires the `Jobs` and `Processes` modules. It manages the Visual Studio Remote Debugging Tools (`msvsmon.exe`) for Visual Studio 2022 (17) and 2026 (18). A running monitor lets a remote debugger run code on this machine; read [security.md](security.md#remote-debugger) first.

Workflow: `get_remote_debugger_status` (is it installed?) -> `install_remote_debugger` (job) -> optionally `prepare_remote_debugger_computer` (firewall) -> `start_remote_debugger` -> connect Visual Studio (Debug > Attach to Process, connection target `host:4026`) -> `stop_remote_debugger`.

Every tool takes `version` (`2022` or `2026`) and `arch` (`x64` default, `arm64`, `x86`).

`get_remote_debugger_status` lists every version and architecture with the discovered `msvsmon.exe` path, how it was found (`standalone`, `ide`, `vswhere`, `registry`, `extra`), its file version and the installer URL, plus each running `msvsmon.exe` with pid, session id, port, whether that port is listening and whether Vestigo started it. The port comes from the process command line (read through CIM) and falls back to the port Vestigo used or the default.

`install_remote_debugger` downloads `https://aka.ms/vs/<17|18>/release/RemoteTools.<amd64|arm64|x86>ret.enu.exe` to a temporary directory under the spool, requires a valid Authenticode signature (`WinVerifyTrust`) whose signer CN or O is `Microsoft Corporation`, and runs it as the service account with `/install /quiet /norestart`. Everything happens inside a job so the call returns a job id at once; the temporary files are removed at the end. It returns `Skipped` when `msvsmon.exe` is already found, unless `force` is true. `Vestigo:RemoteDebugger:InstallerDownloadTimeout` limits the download.

`start_remote_debugger` runs `msvsmon.exe /nostatus /silent /nofirewallwarn /port <port> /timeout <seconds>` in the `interactive` context by default (`elevated` uses the user's linked administrator token). `port` defaults to `Vestigo:RemoteDebugger:DefaultPort` (4026); `timeoutSeconds` (30 to 86400, default 3600) is how long the monitor waits without a connection. `auth` is `windows` (default; `allowUsers` entries become `/allow`) or `none` (`/noauth /anyuser`), which needs `iUnderstandNoAuthIsInsecure: true` and adds a warning to the result and the log. A second instance on a port that is in use is refused with `port_in_use`. The result has the pid, port, the sanitised arguments, whether the port is listening, and the job id.

`stop_remote_debugger` takes a `pid` of a running `msvsmon.exe` or `all: true` for the instances Vestigo started. It posts a close request, waits `gracefulTimeoutSeconds`, then kills the process tree.

`prepare_remote_debugger_computer` runs `msvsmon.exe /prepcomputer /private /quiet` as a job (default `service` context) to configure firewall rules and permissions for private networks.
