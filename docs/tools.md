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

## Jobs

`list_jobs`, `get_job`, `wait_job`, `read_job_output` and `cancel_job` manage background jobs that other tools start. `read_job_output` returns text after a byte `offset`; pass the returned `NextOffset` to continue. Finished jobs are kept for `JobRetention`.
