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

## `copy_path`

Copies a file or directory tree. Source needs Read access, destination needs Write access. Links inside directory trees are skipped.

| Parameter | Type | Description |
| --- | --- | --- |
| `source` | `string` | Source path. |
| `destination` | `string` | Destination path (the new name, not the parent directory). |
| `overwrite` | `bool` | Replace existing destination files. |

## `delete_file_on_reboot`

Schedules a file for deletion at the next reboot (MoveFileEx delay-until-reboot). Needs Write access and administrator rights.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | File path inside a root with Write access. |

## `delete_path`

Deletes a file or directory inside a root with Write access. Refuses to delete a root or a directory containing a root. Non-empty directories need recursive=true.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Path to delete. |
| `recursive` | `bool` | Delete directory contents too. |

## `fetch_file`

Downloads an HTTP(S) URL or copies a UNC file into a root with Write access as a background job. Poll with wait_job and read_job_output; the output ends with the stored file's path, size and SHA-256.

| Parameter | Type | Description |
| --- | --- | --- |
| `source` | `string` | http:// or https:// URL, or a UNC path such as \\server\share\file.sys. |
| `destination` | `string` | Destination file as root:relative\path. |
| `expectedSha256` | `string` | Expected SHA-256 (hex). The file is discarded on mismatch. |
| `overwrite` | `bool` | Replace an existing destination file. |

## `get_capture`

Returns status, counters, and the last error for one capture.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `get_environment_variable`

Reads one environment variable. Machine scope reads the Session Manager registry key and user scope reads the environment of the user logged on at the console; both return the unexpanded value and whether it is REG_EXPAND_SZ. Fails with user_not_available when nobody is logged on.

| Parameter | Type | Description |
| --- | --- | --- |
| `scope` | `string` | Scope: machine, user, or process. |
| `name` | `string` | Variable name. |

## `get_job`

Returns state, exit code, and output size for one job.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |

## `get_upload_url`

Returns the HTTP PUT URL for uploading a file into a root, with curl and Invoke-WebRequest examples. Uploads need Write access to the root.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Destination as root:relative\path. |
| `overwrite` | `bool` | Replace an existing file. |
| `deferToReboot` | `bool` | Replace a locked file at the next reboot instead of renaming it aside. |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPath` | `string` | Absolute or rooted path to a .pdb file. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_directory`

Lists the entries of a directory inside a root with Read access.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Directory as root:relative\path or an absolute path inside a root. |
| `maxEntries` | `int` | Maximum entries to return, 1 to 5000. |

## `list_environment_variables`

Lists environment variables of a scope in name order, capped at 1000 entries.

| Parameter | Type | Description |
| --- | --- | --- |
| `scope` | `string` | Scope: machine, user, or process. |
| `nameContains` | `string` | Only names containing this text (case-insensitive). |
| `includeValues` | `bool` | Include values. Disable to list names only. |

## `list_etw_sessions`

Lists ETW session names currently running on this machine.

No parameters.

## `list_event_logs`

Lists Windows event log channels with record count and enabled state. Channels that deny access are listed without details.

| Parameter | Type | Description |
| --- | --- | --- |
| `nameContains` | `string` | Only channels whose name contains this text (case-insensitive). |
| `includeCounts` | `bool` | Include record counts. Disable for a faster listing. |

## `list_jobs`

Lists background jobs owned by this Vestigo process, newest first.

No parameters.

## `list_roots`

Lists the configured named roots with their access flags and free disk space.

No parameters.

## `make_directory`

Creates a directory, including missing parents, inside a root with Write access.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Directory path. |

## `move_path`

Moves or renames a file or directory. Both paths need Write access. Refuses to move a root or a directory containing a root.

| Parameter | Type | Description |
| --- | --- | --- |
| `source` | `string` | Source path. |
| `destination` | `string` | Destination path (the new name). |
| `overwrite` | `bool` | Replace an existing destination file. |

## `query_event_log`

Queries a Windows event log channel or an .evtx file under a root with Read access. Use the structured filters or a raw XPath, not both. Results are capped by Vestigo:EventLog:MaxEvents. Messages come from the installed publisher and are null when it is missing.

| Parameter | Type | Description |
| --- | --- | --- |
| `channel` | `string` | Channel name such as Application or System, or a path ending in .evtx. |
| `providers` | `string[]` | Provider names to match. |
| `levels` | `string[]` | Levels to match: critical, error, warning, information, verbose. |
| `eventIds` | `int[]` | Event IDs to match. |
| `since` | `string` | Only events at or after this ISO 8601 timestamp. |
| `until` | `string` | Only events at or before this ISO 8601 timestamp. |
| `messageContains` | `string` | Only events whose rendered message contains this text (case-insensitive). |
| `maxEvents` | `int?` | Maximum events to return. Clamped to MaxEvents. |
| `newestFirst` | `bool` | Return the newest events first. |
| `xpath` | `string` | Raw Event Log XPath, for example *[System[EventID=1000]]. Cannot be combined with the structured filters. |
| `includeXml` | `bool` | Include the event XML. |

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

## `read_text_file_since`

Returns complete lines after a cursor from tail_text_file or an earlier call. If the file shrank or was replaced, reading restarts at the beginning and rotated is true. An unterminated last line is held back until it is completed. Continue while hasMore is true.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | File path: root:relative\path or an absolute path inside a root. |
| `offset` | `long` | Cursor offset returned by the previous call. Use 0 to start at the beginning. |
| `identity` | `long` | Cursor identity returned by the previous call. Omit or 0 to skip the replacement check. |
| `maxLines` | `int?` | Maximum lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | Maximum bytes to examine. Clamped to MaxTailBytes. |

## `scan_etl_metadata`

Lists distinct PDB identities referenced by one or more ETL files.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |

## `set_environment_variable`

Changes a persistent environment variable and broadcasts WM_SETTINGCHANGE. Machine scope is a machine-wide configuration change. Names in Vestigo:Environment:ProtectedNames are refused. Append and prepend treat the value as ';'-separated entries and skip entries already present. The value is not echoed back.

| Parameter | Type | Description |
| --- | --- | --- |
| `scope` | `string` | Scope: machine, user, or process. Process only affects the Vestigo service and its children. |
| `name` | `string` | Variable name. |
| `operation` | `string` | Operation: set, unset, append, or prepend. |
| `value` | `string` | Value or list entries. Ignored for unset. |
| `expand` | `bool` | Write REG_EXPAND_SZ so %VAR% references are expanded by readers. List operations keep an existing REG_EXPAND_SZ kind. |

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

## `stat_path`

Returns metadata for a file or directory: size, times, optional SHA-256, PE version info and Authenticode status for exe/dll/sys files.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Path as root:relative\path or an absolute path inside a root with Read access. |
| `computeSha256` | `bool` | Compute the SHA-256 of a file. |

## `stop_capture`

Stops a capture and its ETW session.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `tail_text_file`

Returns the last lines of a text file under a root with Read access. The file is opened with shared access so active logs can be read. Encoding is detected from a UTF-8 or UTF-16 byte order mark, otherwise UTF-8. Returns a cursor for read_text_file_since.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | File path: root:relative\path or an absolute path inside a root. |
| `lines` | `int` | Number of lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | Maximum bytes to examine from the end of the file. Clamped to MaxTailBytes. |

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

