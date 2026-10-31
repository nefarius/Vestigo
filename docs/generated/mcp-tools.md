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

## `close_app`

Politely closes applications by posting WM_CLOSE to their top-level windows. Select by pid, processName and/or titleRegex (filters combine: pid or name, narrowed by title). Waits gracefulTimeoutSeconds, then reports which processes are still alive; it never kills them. context 'interactive' (default) acts on the console user's desktop.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id. |
| `processName` | `string` | Process name, with or without .exe. |
| `titleRegex` | `string` | .NET regular expression matched against window titles (case-insensitive). |
| `gracefulTimeoutSeconds` | `int` | Seconds to wait for exit after WM_CLOSE, 0 to 600. |
| `context` | `string` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | Interactive only: use the elevated token so elevated windows can be closed. |

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

## `create_device_node`

Creates a ROOT-enumerated device node with a hardware ID and device class. Install a driver on it afterwards with install_driver.

| Parameter | Type | Description |
| --- | --- | --- |
| `className` | `string` | Device class name, for example System. |
| `classGuid` | `string` | Device class GUID. |
| `hardwareId` | `string` | Hardware ID of the new node, for example Root\MyDriver. |
| `noDuplicates` | `bool` | Do nothing when a node with this hardware ID already exists. |

## `create_driver_service`

Creates a kernel driver service through nefcon. The .sys binary must be an existing file inside a configured root that allows Execute. The service starts on demand; use start_service afterwards.

| Parameter | Type | Description |
| --- | --- | --- |
| `serviceName` | `string` | New service name. |
| `binaryPath` | `string` | Driver .sys path: root:relative\path or an absolute path inside a root that allows Execute. |
| `displayName` | `string` | Display name. Defaults to the service name. |

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

## `delete_service`

Deletes a kernel driver service through nefcon. Win32 services are refused. Stop the driver first.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Driver service name (not a path). |

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

## `fetch_file`

Downloads an HTTP(S) URL or copies a UNC file into a root with Write access as a background job. Poll with wait_job and read_job_output; the output ends with the stored file's path, size and SHA-256.

| Parameter | Type | Description |
| --- | --- | --- |
| `source` | `string` | http:// or https:// URL, or a UNC path such as \\server\share\file.sys. |
| `destination` | `string` | Destination file as root:relative\path. |
| `expectedSha256` | `string` | Expected SHA-256 (hex). The file is discarded on mismatch. |
| `overwrite` | `bool` | Replace an existing destination file. |

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

## `get_remote_debugger_status`

Reports, for each Visual Studio version (2022, 2026) and architecture (x64, arm64, x86), whether msvsmon.exe is installed (path, file version, how it was found), plus running msvsmon processes with pid, session id, port (from the command line when readable) and whether that port is listening.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | Optional filter: '2022' or '2026'. |
| `arch` | `string` | Optional filter: 'x64', 'arm64' or 'x86'. |

## `get_service`

Returns state, start type, binary path, type (including KERNEL_DRIVER), error control, dependencies, exit codes, and the process id while running.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name (not the display name). |

## `get_upload_url`

Returns the HTTP PUT URL for uploading a file into a root, with curl and Invoke-WebRequest examples. Uploads need Write access to the root.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Destination as root:relative\path. |
| `overwrite` | `bool` | Replace an existing file. |
| `deferToReboot` | `bool` | Replace a locked file at the next reboot instead of renaming it aside. |

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

## `install_remote_debugger`

Installs the Visual Studio Remote Debugging Tools as a job: downloads the official installer (aka.ms) into the spool, requires a valid Microsoft Corporation Authenticode signature, then runs it silently (/install /quiet /norestart) as the service account. Returns the job id; poll with wait_job or read_job_output. Skipped when msvsmon is already installed unless force is true.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `force` | `bool` | Reinstall even when msvsmon.exe is already present. |

## `kill_process`

Terminates a process by pid or exact name. entireTree also kills its descendants. Refuses the Vestigo process, pid 0 and pid 4.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id. |
| `processName` | `string` | Exact process name, with or without .exe. Every match is killed. |
| `entireTree` | `bool` | Also kill child processes. |

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

## `list_processes`

Lists running processes with pid, name, session id and start time. Filters are optional.

| Parameter | Type | Description |
| --- | --- | --- |
| `nameFilter` | `string` | Case-insensitive substring of the process name. |
| `pid` | `int?` | Only this process id. |
| `sessionId` | `int?` | Only this session id (0 is services, the console session is usually 1). |
| `limit` | `int` | Maximum rows, 1 to 2000. |

## `list_roots`

Lists the configured named roots with their access flags and free disk space.

No parameters.

## `list_services`

Lists Win32 services and kernel/file-system drivers with state, start type, image path, account, and dependencies, sorted by name.

| Parameter | Type | Description |
| --- | --- | --- |
| `kind` | `string` | win32, driver or all. |
| `nameContains` | `string` | Case-insensitive substring of the service name or display name. |
| `state` | `string` | Only services in this state: stopped, running, paused, start_pending, stop_pending, continue_pending or pause_pending. |
| `limit` | `int` | Maximum entries to return, 1 to 1000. |

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

## `prepare_remote_debugger_computer`

Runs 'msvsmon /prepcomputer /private /quiet' as a job: configures the Windows firewall and permissions for remote debugging on private networks. It changes firewall rules; needs the service context (default). Returns the job id.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `context` | `string` | 'service' (default) or 'interactive'. |

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

## `restart_service`

Stops a service (waiting for Stopped) and starts it again (waiting for Running). Dependents stopped by stopDependents are not restarted.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `stopDependents` | `bool` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | Seconds to wait for each phase, 1 to 300. |

## `run_process`

Starts a process as a job. path is a file under a root with Execute access (name:relative\path or absolute), or a bare name such as cmd.exe when AllowPathLookup is enabled. .ps1 files run through pwsh.exe, else powershell.exe. context 'service' runs as the Vestigo service account; 'interactive' runs as the console user on winsta0\default (elevated: true uses the linked administrator token). stdout and stderr go to the job output. wait > 0 waits that many seconds and returns the output tail; wait 0 returns the job id immediately.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Executable or script path, or a bare name resolved from PATH. |
| `args` | `string[]` | Command-line arguments, one array element per argument. |
| `workingDirectory` | `string` | Working directory under a root with Read access. Defaults to the executable's directory. |
| `env` | `Dictionary`2` | Environment variable overrides. |
| `context` | `string` | 'service' (default) or 'interactive'. |
| `elevated` | `bool` | Interactive only: run with the elevated linked token. |
| `wait` | `int` | Seconds to wait (0 to 300) before returning. 0 returns the job id immediately. |

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

## `start_remote_debugger`

Starts msvsmon.exe with /nostatus /silent /nofirewallwarn, /port, /timeout and the chosen authentication. Default context is 'interactive' (the console user's desktop), so attach rights match that user. auth 'windows' (default) authenticates callers; allowUsers adds /allow entries. auth 'none' disables authentication completely and requires iUnderstandNoAuthIsInsecure: true. Refuses a second instance on the same port. Connect Visual Studio to <host>:<port> afterwards.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `port` | `int?` | TCP port; defaults to Vestigo:RemoteDebugger:DefaultPort (4026). |
| `timeoutSeconds` | `int` | Seconds msvsmon waits without a connection before exiting, 30 to 86400. |
| `auth` | `string` | 'windows' (default) or 'none'. |
| `iUnderstandNoAuthIsInsecure` | `bool` | Required to be true when auth is 'none'. Acknowledges that anyone reaching the port can run code. |
| `allowUsers` | `string[]` | Windows users allowed to connect (DOMAIN\user or user@domain), each passed as /allow. Only with auth 'windows'. |
| `context` | `string` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | Interactive only: run with the elevated linked token so elevated processes can be debugged. |

## `start_service`

Starts a service or driver and waits until it is running. Starting an unsigned driver may fail unless test-signing is enabled.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `arguments` | `string[]` | Optional start arguments passed to the service. |
| `timeoutSeconds` | `int` | Seconds to wait for Running, 0 to 300. 0 returns without waiting. |

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

## `stop_remote_debugger`

Stops msvsmon.exe by pid, or every instance started by Vestigo (all: true). Tries a graceful close, waits gracefulTimeoutSeconds, then kills the process. Only msvsmon.exe processes are accepted.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id of an msvsmon.exe. |
| `all` | `bool` | Stop all instances started by Vestigo. |
| `gracefulTimeoutSeconds` | `int` | Seconds to wait after the graceful close before killing, 0 to 120. |

## `stop_service`

Stops a service or driver and waits until it is stopped.

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | Service name. |
| `stopDependents` | `bool` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | Seconds to wait for Stopped, 0 to 300. 0 returns without waiting. |

## `tail_text_file`

Returns the last lines of a text file under a root with Read access. The file is opened with shared access so active logs can be read. Encoding is detected from a UTF-8 or UTF-16 byte order mark, otherwise UTF-8. Returns a cursor for read_text_file_since.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | File path: root:relative\path or an absolute path inside a root. |
| `lines` | `int` | Number of lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | Maximum bytes to examine from the end of the file. Clamped to MaxTailBytes. |

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

