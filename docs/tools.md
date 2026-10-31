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
