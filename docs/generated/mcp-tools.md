# MCP tools

Generated from `McpServerTool` attributes. Do not edit by hand.

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

## `get_capture`

Returns status, counters, and the last error for one capture.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

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

## `read_capture`

Reads a page of decoded NDJSON events from a capture spool.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |
| `afterSequence` | `long` | Return events after this sequence. Use 0 to start at the beginning. |
| `limit` | `int?` | Maximum events to return. The server clamps this to its configured batch size. |

## `scan_etl_metadata`

Lists distinct PDB identities referenced by one or more ETL files.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |

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

## `stop_capture`

Stops a capture and its ETW session.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `validate_decoding_sources`

Loads PDB and TMF files and reports whether they can decode WPP messages.

| Parameter | Type | Description |
| --- | --- | --- |
| `symbolPaths` | `string[]` | PDB and TMF paths inside an allowed input root. |

