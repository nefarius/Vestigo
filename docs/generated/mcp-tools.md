# MCP tools

Generated from `McpServerTool` attributes. Do not edit by hand.

## `cancel_job`

Requests cancellation of a running job. Process jobs kill their process tree.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |

## `convert_etl`

Decodes ETL files to a JSON or NDJSON file inside an allowed output root and returns a summary.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |
| `outputPath` | `string` | Output .json or .ndjson path inside an allowed output root. |
| `format` | `string` | json or ndjson. |
| `symbolPaths` | `string[]` | Optional explicit PDB and TMF paths. |
| `resolveSymbols` | `bool` | Resolve PDB references through the configured symbol cache and server. |
| `overwrite` | `bool` | Replace an existing output file. |

## `create_driver_service`

Creates a kernel driver service through nefcon. The .sys binary must be an existing file inside a configured root that allows Execute. The service starts on demand; use start_service afterwards.

| Parameter | Type | Description |
| --- | --- | --- |
| `serviceName` | `string` | New service name. |
| `binaryPath` | `string` | Driver .sys path: root:relative\path or an absolute path inside a root that allows Execute. |
| `displayName` | `string` | Display name. Defaults to the service name. |

## `delete_service`

Deletes a kernel driver service through nefcon. Win32 services are refused. Stop the driver first.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Driver service name (not a path). |

## `get_capture`

Returns status, counters, and the last error for one capture.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `get_job`

Returns state, exit code, and output size for one job.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |

## `get_service`

Returns state, start type, binary path, type (including KERNEL_DRIVER), error control, dependencies, exit codes, and the process id while running.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name (not the display name). |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPath` | `string` | Absolute or rooted path to a .pdb file. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_etw_sessions`

Lists ETW session names currently running on this machine.

No parameters.

## `list_jobs`

Lists background jobs owned by this Vestigo process, newest first.

No parameters.

## `list_services`

Lists Win32 services and kernel/file-system drivers with state, start type, image path, account, and dependencies, sorted by name.

| Parameter | Type | Description |
| --- | --- | --- |
| `kind` | `string` | win32, driver or all. |
| `nameContains` | `string` | Case-insensitive substring of the service name or display name. |
| `state` | `string` | Only services in this state: stopped, running, paused, start_pending, stop_pending, continue_pending or pause_pending. |
| `limit` | `int` | Maximum entries to return, 1 to 1000. |

## `read_capture`

Reads a page of decoded NDJSON events from a capture spool.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |
| `afterSequence` | `long` | Return events after this sequence. Use 0 to start at the beginning. |
| `limit` | `int?` | Maximum events to return. The server clamps this to its configured batch size. |

## `read_job_output`

Reads a page of text output from a job. Pass the returned NextOffset to continue.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |
| `offset` | `long` | Byte offset to read from. Use 0 for the beginning. |
| `limit` | `int?` | Maximum bytes to return. Clamped to MaxJobReadBytes. |

## `restart_service`

Stops a service (waiting for Stopped) and starts it again (waiting for Running). Dependents stopped by stopDependents are not restarted.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `stopDependents` | `bool` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | Seconds to wait for each phase, 1 to 300. |

## `scan_etl_metadata`

Lists distinct PDB identities referenced by one or more ETL files.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |

## `set_service_start_type`

Sets the start type: boot, system, auto, demand or disabled. boot and system are only valid for drivers.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `startType` | `string` | boot, system, auto, demand or disabled. |

## `start_capture`

Starts a managed realtime WPP capture. Events are spooled locally; use read_capture to page them.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPaths` | `string[]` | PDB paths whose WPP control GUIDs should be enabled. |
| `tmfPaths` | `string[]` | TMF paths used only for message decoding. |
| `providerIds` | `string[]` | Additional ETW provider GUIDs. |
| `level` | `string` | Trace level: None, Critical, Error, Warning, Information, or Verbose. |
| `matchAnyKeyword` | `string` | Match-any keyword bitmask. Omit for all keywords. |
| `matchAllKeyword` | `string` | Match-all keyword bitmask. Omit for no match-all filter. |
| `duration` | `string` | Maximum capture duration, for example 00:05:00. Capped by server configuration. |

## `start_service`

Starts a service or driver and waits until it is running. Starting an unsigned driver may fail unless test-signing is enabled.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `arguments` | `string[]` | Optional start arguments passed to the service. |
| `timeoutSeconds` | `int` | Seconds to wait for Running, 0 to 300. 0 returns without waiting. |

## `stop_capture`

Stops a capture and its ETW session.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `stop_service`

Stops a service or driver and waits until it is stopped.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `stopDependents` | `bool` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | Seconds to wait for Stopped, 0 to 300. 0 returns without waiting. |

## `validate_decoding_sources`

Loads PDB and TMF files and reports whether they can decode WPP messages.

| Parameter | Type | Description |
| --- | --- | --- |
| `symbolPaths` | `string[]` | PDB and TMF paths inside an allowed input root. |

## `wait_job`

Waits up to the given number of seconds for a job to finish and returns its state.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |
| `timeoutSeconds` | `int` | Maximum seconds to wait, 0 to 300. |

