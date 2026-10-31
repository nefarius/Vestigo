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
