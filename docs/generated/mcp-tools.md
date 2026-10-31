# MCP tools

Generated from `McpServerTool` attributes. Do not edit by hand.

## `add_class_filter`

Adds an existing driver service to a device class's upper or lower filter list.

| Parameter | Type | Description |
| --- | --- | --- |
| `classGuid` | `string` | Device class GUID. |
| `serviceName` | `string` | Filter driver service name. |
| `position` | `string` | upper or lower. |

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

## `create_device_node`

Creates a ROOT-enumerated device node with a hardware ID and device class. Install a driver on it afterwards with install_driver.

| Parameter | Type | Description |
| --- | --- | --- |
| `className` | `string` | Device class name, for example System. |
| `classGuid` | `string` | Device class GUID. |
| `hardwareId` | `string` | Hardware ID of the new node, for example Root\MyDriver. |
| `noDuplicates` | `bool` | Do nothing when a node with this hardware ID already exists. |

## `disable_device`

Disables a device by instance ID.

| Parameter | Type | Description |
| --- | --- | --- |
| `instanceId` | `string` | Device instance ID. |

## `enable_device`

Enables a device by instance ID.

| Parameter | Type | Description |
| --- | --- | --- |
| `instanceId` | `string` | Device instance ID. |

## `find_devices_by_hardware_id`

Finds devices in a device class whose hardware IDs match, and returns their instance IDs.

| Parameter | Type | Description |
| --- | --- | --- |
| `classGuid` | `string` | Device class GUID to search in. |
| `hardwareId` | `string` | Hardware ID to match. |
| `presentOnly` | `bool` | Only currently present devices. |
| `allowPartial` | `bool` | Match substrings instead of the exact ID. |

## `find_devices_by_interface`

Finds devices that expose a device interface GUID and returns their instance IDs and device paths.

| Parameter | Type | Description |
| --- | --- | --- |
| `interfaceGuid` | `string` | Device interface GUID. |
| `presentOnly` | `bool` | Only currently present devices. False also returns phantom devices. |

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

## `inf_default_install`

Runs the [DefaultInstall] section of an INF inside a readable root (nefcon --inf-default-install).

| Parameter | Type | Description |
| --- | --- | --- |
| `infPath` | `string` | INF path inside an allowed root. |

## `inf_default_uninstall`

Runs the [DefaultUninstall] section of an INF inside a readable root (nefcon --inf-default-uninstall).

| Parameter | Type | Description |
| --- | --- | --- |
| `infPath` | `string` | INF path inside an allowed root. |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPath` | `string` | Absolute or rooted path to a .pdb file. |

## `install_driver`

Installs a driver from an INF inside a readable root. With hardwareId it creates the ROOT device node and installs the driver on it (devcon install); without it the package is only staged and installed. The result has exitCode, outputTail and rebootRequired.

| Parameter | Type | Description |
| --- | --- | --- |
| `infPath` | `string` | INF path inside an allowed root (name:relative\path or absolute). |
| `hardwareId` | `string` | Optional hardware ID, for example Root\MyDriver. Enables the duplicate options. |
| `noDuplicates` | `bool` | Skip creating the node when a device with this hardware ID already exists. Needs hardwareId. |
| `removeDuplicates` | `bool` | Remove duplicate device nodes. Needs hardwareId and noDuplicates. |

## `install_filter_driver`

Installs a filter driver from an INF inside a readable root and registers it as a class filter (nefcon --install-filter-driver).

| Parameter | Type | Description |
| --- | --- | --- |
| `infPath` | `string` | INF path inside an allowed root. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_etw_sessions`

Lists ETW session names currently running on this machine.

No parameters.

## `list_jobs`

Lists background jobs owned by this Vestigo process, newest first.

No parameters.

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

## `remove_class_filter`

Removes a driver service from a device class's upper or lower filter list.

| Parameter | Type | Description |
| --- | --- | --- |
| `classGuid` | `string` | Device class GUID. |
| `serviceName` | `string` | Filter driver service name. |
| `position` | `string` | upper or lower. |

## `remove_device_node`

Removes a device node by hardware ID and class GUID (nefcon --remove-device-node).

| Parameter | Type | Description |
| --- | --- | --- |
| `hardwareId` | `string` | Hardware ID of the node. |
| `classGuid` | `string` | Device class GUID. |

## `remove_driver_store_package`

Removes driver store packages by original INF file name (for example mydriver.inf). Optional class GUID and service name narrow the match. nefcon offers no all-versions switch here.

| Parameter | Type | Description |
| --- | --- | --- |
| `infName` | `string` | Original INF base name, no directory, for example mydriver.inf. |
| `classGuid` | `string` | Optional device class GUID to narrow the match. |
| `serviceName` | `string` | Optional driver service name to narrow the match. |

## `restart_device`

Restarts a device (remove and re-enumerate) by instance ID, for example after installing a filter driver.

| Parameter | Type | Description |
| --- | --- | --- |
| `instanceId` | `string` | Device instance ID, for example ROOT\SYSTEM\0001. |

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

## `uninstall_driver`

Uninstalls the driver described by an INF inside a readable root (nefcon --uninstall-driver).

| Parameter | Type | Description |
| --- | --- | --- |
| `infPath` | `string` | INF path inside an allowed root. |

## `uninstall_filter_driver`

Removes a class filter and its driver service. With infPath it also purges that driver store package; allVersions then purges every version of it.

| Parameter | Type | Description |
| --- | --- | --- |
| `classGuid` | `string` | Device class GUID the filter is registered in. |
| `serviceName` | `string` | Filter driver service name. |
| `position` | `string` | upper or lower. |
| `infPath` | `string` | Optional INF path inside an allowed root whose driver store package is also removed. |
| `allVersions` | `bool` | Remove all versions of the driver store package. Needs infPath. |

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

