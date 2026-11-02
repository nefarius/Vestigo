# MCP tools

Generated from `McpServerTool` attributes. Do not edit by hand.

## `add_class_filter`

Adds an existing driver service to a device class's upper or lower filter list.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `classGuid` | `string` | yes |  | Device class GUID. |
| `serviceName` | `string` | yes |  | Filter driver service name. |
| `position` | `string` | yes |  | upper or lower. |

## `attach_etw_session`

Attaches read-only to an existing realtime ETW session (not Vestigo-owned) allowed by Vestigo:AttachableSessions and decodes its events live. Read with read_capture; stop_capture only detaches and never stops the session.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sessionName` | `string` | yes |  | Exact name of a running ETW session, as listed by list_etw_sessions. |
| `symbolPaths` | `string[]` | no | `null` | Optional PDB and TMF paths inside an allowed input root used to decode WPP messages. |
| `duration` | `string` | no | `null` | Maximum attach duration, for example 00:05:00. Capped by server configuration. |

## `cancel_job`

Requests cancellation of a running job. Process jobs kill their process tree.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |

## `close_app`

Politely closes applications by posting WM_CLOSE to their top-level windows. Select by pid, processName and/or titleRegex (filters combine: pid or name, narrowed by title). Waits gracefulTimeoutSeconds, then reports which processes are still alive; it never kills them. context 'interactive' (default) acts on the console user's desktop.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int?` | no | `null` | Process id. |
| `processName` | `string` | no | `null` | Process name, with or without .exe. |
| `titleRegex` | `string` | no | `null` | .NET regular expression matched against window titles (case-insensitive). |
| `gracefulTimeoutSeconds` | `int` | no | `10` | Seconds to wait for exit after WM_CLOSE, 0 to 600. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token so elevated windows can be closed. |

## `convert_etl`

Decodes ETL files to a JSON or NDJSON file inside an allowed output root and returns a summary, plus a download link when the output also lies in a Files root with Read access.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `etlPaths` | `string[]` | yes |  | ETL paths inside an allowed input root. |
| `outputPath` | `string` | yes |  | Output .json or .ndjson path inside an allowed output root. |
| `format` | `string` | no | `ndjson` | json or ndjson. |
| `symbolPaths` | `string[]` | no | `null` | Optional explicit PDB and TMF paths. |
| `resolveSymbols` | `bool` | no | `false` | Resolve PDB references through the configured symbol cache and server. |
| `overwrite` | `bool` | no | `false` | Replace an existing output file. |

## `copy_path`

Copies a file or directory tree. Source needs Read access, destination needs Write access. Links inside directory trees are skipped.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes |  | Source path. |
| `destination` | `string` | yes |  | Destination path (the new name, not the parent directory). |
| `overwrite` | `bool` | no | `false` | Replace existing destination files. |

## `create_device_node`

Creates a ROOT-enumerated device node with a hardware ID and device class. Install a driver on it afterwards with install_driver.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `className` | `string` | yes |  | Device class name, for example System. |
| `classGuid` | `string` | yes |  | Device class GUID. |
| `hardwareId` | `string` | yes |  | Hardware ID of the new node, for example Root\MyDriver. |
| `noDuplicates` | `bool` | no | `false` | Do nothing when a node with this hardware ID already exists. |

## `create_driver_service`

Creates a kernel driver service through nefcon. The .sys binary must be an existing file inside a configured root that allows Execute. The service starts on demand; use start_service afterwards.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `serviceName` | `string` | yes |  | New service name. |
| `binaryPath` | `string` | yes |  | Driver .sys path: root:relative\path or an absolute path inside a root that allows Execute. |
| `displayName` | `string` | no | `null` | Display name. Defaults to the service name. |

## `delete_file_on_reboot`

Schedules a file for deletion at the next reboot (MoveFileEx delay-until-reboot). Needs Write access and administrator rights.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path inside a root with Write access. |

## `delete_path`

Deletes a file or directory inside a root with Write access. Refuses to delete a root or a directory containing a root. Non-empty directories need recursive=true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Path to delete. |
| `recursive` | `bool` | no | `false` | Delete directory contents too. |

## `delete_scheduled_task`

Permanently deletes a task definition. Requires Vestigo:ScheduledTasks:AllowChanges; tasks under Vestigo:ScheduledTasks:ProtectedPaths are refused. Returns the deleted task's last known state.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |

## `delete_service`

Deletes a kernel driver service through nefcon. Win32 services are refused. Stop the driver first.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Driver service name (not a path). |

## `disable_device`

Disables a device by instance ID.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID. |

## `disable_scheduled_task`

Disables a task so its triggers no longer fire. Requires Vestigo:ScheduledTasks:AllowChanges; tasks under Vestigo:ScheduledTasks:ProtectedPaths are refused.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |

## `enable_device`

Enables a device by instance ID.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID. |

## `enable_scheduled_task`

Enables a task. Requires Vestigo:ScheduledTasks:AllowChanges.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |

## `fetch_file`

Downloads an HTTP(S) URL or copies a UNC file into a root with Write access as a background job. Poll with wait_job and read_job_output; the output ends with the stored file's path, size and SHA-256.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes |  | http:// or https:// URL, or a UNC path such as \\server\share\file.sys. |
| `destination` | `string` | yes |  | Destination file as root:relative\path. |
| `expectedSha256` | `string` | no | `null` | Expected SHA-256 (hex). The file is discarded on mismatch. |
| `overwrite` | `bool` | no | `false` | Replace an existing destination file. |

## `find_devices`

Lists device nodes, filtered by name substring, hardware ID, and device class. Returns instance ID, description, class, hardware IDs, service, driver INF, and whether the device is present. All filters are optional and combined with AND.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameContains` | `string` | no | `null` | Case-insensitive substring of the device description. |
| `hardwareId` | `string` | no | `null` | Case-insensitive hardware ID; exact match unless it ends with '*' (prefix match). |
| `deviceClass` | `string` | no | `null` | Device class name (for example USB) or class GUID. |
| `presentOnly` | `bool` | no | `true` | Only currently present devices. False also lists phantom devices. |
| `limit` | `int` | no | `100` | Maximum devices to return. Clamped to 1-500. |

## `find_devices_by_hardware_id`

Finds devices in a device class whose hardware IDs match, and returns their instance IDs.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `classGuid` | `string` | yes |  | Device class GUID to search in. |
| `hardwareId` | `string` | yes |  | Hardware ID to match. |
| `presentOnly` | `bool` | no | `true` | Only currently present devices. |
| `allowPartial` | `bool` | no | `false` | Match substrings instead of the exact ID. |

## `find_devices_by_interface`

Finds devices that expose a device interface GUID and returns their instance IDs and device paths.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `interfaceGuid` | `string` | yes |  | Device interface GUID. |
| `presentOnly` | `bool` | no | `true` | Only currently present devices. False also returns phantom devices. |

## `get_boot_security`

Returns UEFI vs legacy BIOS, Secure Boot, BCD test-signing (configured, takes effect at next boot), the running kernel test-signing state, WHQL developer test mode, and rebootPending when the BCD value and the running state differ. Notes cover Secure Boot, a pending reboot, and that BCD changes apply only to the default boot entry.

No parameters.

## `get_capture`

Returns status, counters, and the last error for one capture.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |

## `get_class_filters`

Returns the upper and lower filter driver service names registered for a device setup class GUID (read from the registry).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `classGuid` | `string` | yes |  | Device setup class GUID. |

## `get_debug_capture`

Returns status, counters, and the last error for one debug-output capture.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |

## `get_device`

Returns details of one device by instance ID: hardware and compatible IDs, class, service, present flag, problem code and devnode status (problemCode 0 means no problem), and installed driver key, description, provider, version, date and INF.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID, for example ROOT\SYSTEM\0001. |

## `get_download_url`

Returns an HTTP GET URL for downloading an existing file from a root with Read access, with curl and Invoke-WebRequest examples. When API tokens are configured the URL carries a one-time ticket that expires and stops working after one successful download, so the API token never has to be shared.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File as root:relative\path or an absolute path inside a root with Read access. |

## `get_environment_variable`

Reads one environment variable. Machine scope reads the Session Manager registry key and user scope reads the environment of the user logged on at the console; both return the unexpanded value and whether it is REG_EXPAND_SZ. Fails with user_not_available when nobody is logged on.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `scope` | `string` | yes |  | Scope: machine, user, or process. |
| `name` | `string` | yes |  | Variable name. |

## `get_installed_software`

Returns one entry by id (from list_installed_software) with the silent uninstall command Vestigo would run, or the reason none is available.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Entry id such as machine-x64:{KeyName}. |

## `get_job`

Returns state, exit code, and output size for one job.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |

## `get_remote_debugger_status`

Reports, for each Visual Studio version (2022, 2026) and architecture (x64, arm64, x86), whether msvsmon.exe is installed (path, file version, how it was found), plus running msvsmon processes with pid, session id, port (from the command line when readable) and whether that port is listening.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `version` | `string` | no | `null` | Optional filter: '2022' or '2026'. |
| `arch` | `string` | no | `null` | Optional filter: 'x64', 'arm64' or 'x86'. |

## `get_scheduled_task`

Returns one task with its principal, actions, triggers, key settings, and the full definition XML. Arguments and XML can contain secrets.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |

## `get_scheduled_task_history`

Returns recent events for one task from the Microsoft-Windows-TaskScheduler/Operational log, newest first. Fails with history_disabled when that channel is not enabled.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |
| `limit` | `int` | no | `50` | Maximum events to return, 1 to 1000. |

## `get_service`

Returns state, start type, binary path, type (including KERNEL_DRIVER), error control, dependencies, exit codes, and the process id while running.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name (not the display name). |

## `get_system_info`

Returns the host Windows name, edition, display version (for example 24H2), version string, major/minor/build/UBR, whether it is Windows 11 or Server, whether the install was in-place upgraded, OS and processor bitness, ARM64, UAC disabled, and the machine name. One failed probe becomes a null field.

No parameters.

## `get_upload_url`

Returns the HTTP PUT URL for uploading a file into a root, with curl and Invoke-WebRequest examples. Uploads need Write access to the root. When API tokens are configured the URL carries a one-time ticket bound to this path and these flags that expires and stops working after one successful upload, so the API token never has to be shared.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Destination as root:relative\path. |
| `overwrite` | `bool` | no | `false` | Replace an existing file. |
| `deferToReboot` | `bool` | no | `false` | Replace a locked file at the next reboot instead of renaming it aside. |

## `inf_default_install`

Runs the [DefaultInstall] section of an INF inside a readable root (nefcon --inf-default-install).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infPath` | `string` | yes |  | INF path inside an allowed root. |

## `inf_default_uninstall`

Runs the [DefaultUninstall] section of an INF inside a readable root (nefcon --inf-default-uninstall).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infPath` | `string` | yes |  | INF path inside an allowed root. |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pdbPath` | `string` | yes |  | Absolute or rooted path to a .pdb file. |

## `install_driver`

Installs a driver from an INF inside a readable root. With hardwareId it creates the ROOT device node and installs the driver on it (devcon install); without it the package is only staged and installed. The result has exitCode, outputTail and rebootRequired.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infPath` | `string` | yes |  | INF path inside an allowed root (name:relative\path or absolute). |
| `hardwareId` | `string` | no | `null` | Optional hardware ID, for example Root\MyDriver. Enables the duplicate options. |
| `noDuplicates` | `bool` | no | `false` | Skip creating the node when a device with this hardware ID already exists. Needs hardwareId. |
| `removeDuplicates` | `bool` | no | `false` | Remove duplicate device nodes. Needs hardwareId and noDuplicates. |

## `install_filter_driver`

Installs a filter driver from an INF inside a readable root and registers it as a class filter (nefcon --install-filter-driver).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infPath` | `string` | yes |  | INF path inside an allowed root. |

## `install_remote_debugger`

Installs the Visual Studio Remote Debugging Tools as a job: downloads the official installer (aka.ms) into the spool, requires a valid Microsoft Corporation Authenticode signature, then runs it silently (/install /quiet /norestart) as the service account. Returns the job id; poll with wait_job or read_job_output. Skipped when msvsmon is already installed unless force is true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `version` | `string` | yes |  | '2022' or '2026'. |
| `arch` | `string` | no | `x64` | 'x64' (default), 'arm64' or 'x86'. |
| `force` | `bool` | no | `false` | Reinstall even when msvsmon.exe is already present. |

## `kill_process`

Terminates a process by pid or exact name. entireTree also kills its descendants. Refuses the Vestigo process, pid 0 and pid 4.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int?` | no | `null` | Process id. |
| `processName` | `string` | no | `null` | Exact process name, with or without .exe. Every match is killed. |
| `entireTree` | `bool` | no | `false` | Also kill child processes. |

## `list_audio_endpoints`

Lists Windows audio playback (render) and recording (capture) endpoints with friendly name, state, form factor, default-role flags, volume, mute, a short peak-meter sample, and the processes currently playing or recording. context 'interactive' (default) reads the console user's session through the session helper.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flow` | `string` | no | `all` | 'all' (default), 'render' (speakers) or 'capture' (microphones). |
| `state` | `string` | no | `active` | 'active' (default) or 'all' (includes disabled, unplugged and not-present). |
| `id` | `string` | no | `null` | Return only the endpoint with this id. |
| `includeSessions` | `bool` | no | `true` | Include per-process audio sessions. Defaults to true. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_debug_captures`

Lists debug-output captures owned by this Vestigo process.

No parameters.

## `list_directory`

Lists the entries of a directory inside a root with Read access.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Directory as root:relative\path or an absolute path inside a root. |
| `maxEntries` | `int` | no | `500` | Maximum entries to return, 1 to 5000. |

## `list_displays`

Lists GDI displays (\\.\DISPLAYn) with bounds, work area, DPI, and the physical monitors currently driven by each source, including cloned outputs. context 'interactive' (default) uses the console user's session.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |

## `list_driver_store`

Lists driver packages staged in the driver store: published name (oemNN.inf), original INF, provider, class, version, date, architecture, and bound device instance IDs. Third-party packages only unless includeInbox is true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameContains` | `string` | no | `null` | Case-insensitive substring of the published or original INF name. |
| `provider` | `string` | no | `null` | Case-insensitive substring of the provider. |
| `deviceClass` | `string` | no | `null` | Device class name (for example Net) or class GUID. |
| `includeInbox` | `bool` | no | `false` | Also list inbox packages that ship with Windows. |
| `limit` | `int` | no | `100` | Maximum packages to return. Clamped to 1-500. |

## `list_environment_variables`

Lists environment variables of a scope in name order, capped at 1000 entries.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `scope` | `string` | yes |  | Scope: machine, user, or process. |
| `nameContains` | `string` | no | `null` | Only names containing this text (case-insensitive). |
| `includeValues` | `bool` | no | `true` | Include values. Disable to list names only. |

## `list_etw_sessions`

Lists ETW session names currently running on this machine.

No parameters.

## `list_event_logs`

Lists Windows event log channels with record count and enabled state. Channels that deny access are listed without details.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameContains` | `string` | no | `null` | Only channels whose name contains this text (case-insensitive). |
| `includeCounts` | `bool` | no | `true` | Include record counts. Disable for a faster listing. |

## `list_installed_software`

Lists installed programs from the Uninstall registry keys (machine 64/32-bit and loaded user hives) with version, publisher, install location, uninstall strings, and detected installer type, sorted by name.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameContains` | `string` | no | `null` | Case-insensitive substring of the display name. |
| `publisherContains` | `string` | no | `null` | Case-insensitive substring of the publisher. |
| `scope` | `string` | no | `all` | machine, user or all. |
| `includeSystemComponents` | `bool` | no | `false` | Include entries flagged SystemComponent. |
| `includeUpdates` | `bool` | no | `false` | Include updates and patches that belong to a parent product. |
| `limit` | `int` | no | `200` | Maximum entries to return, 1 to 1000. |

## `list_jobs`

Lists background jobs owned by this Vestigo process, newest first.

No parameters.

## `list_processes`

Lists running processes with pid, name, session id and start time. Filters are optional.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameFilter` | `string` | no | `null` | Case-insensitive substring of the process name. |
| `pid` | `int?` | no | `null` | Only this process id. |
| `sessionId` | `int?` | no | `null` | Only this session id (0 is services, the console session is usually 1). |
| `limit` | `int` | no | `500` | Maximum rows, 1 to 2000. |

## `list_roots`

Lists the configured named roots with their access flags and free disk space.

No parameters.

## `list_scheduled_tasks`

Lists scheduled tasks with state, enabled flag, last and next run time, last result, author, and description, sorted by path.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `folder` | `string` | no | `\` | Folder to list, such as \ or \Microsoft\Windows. |
| `recurse` | `bool` | no | `true` | Include tasks in subfolders. |
| `includeHidden` | `bool` | no | `false` | Include hidden tasks. |
| `nameContains` | `string` | no | `null` | Case-insensitive substring of the task path. |
| `state` | `string` | no | `null` | Only tasks in this state: unknown, disabled, queued, ready or running. |
| `limit` | `int` | no | `200` | Maximum entries to return, 1 to 1000. |

## `list_services`

Lists Win32 services and kernel/file-system drivers with state, start type, image path, account, and dependencies, sorted by name.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `kind` | `string` | no | `all` | win32, driver or all. |
| `nameContains` | `string` | no | `null` | Case-insensitive substring of the service name or display name. |
| `state` | `string` | no | `null` | Only services in this state: stopped, running, paused, start_pending, stop_pending, continue_pending or pause_pending. |
| `limit` | `int` | no | `200` | Maximum entries to return, 1 to 1000. |

## `list_windows`

Lists Win32 windows on the chosen desktop with owning process, parent and owner, physical-pixel bounds, and the GDI display they are rendered on. Filters combine: pid or processName, narrowed by titleRegex and className. context 'interactive' (default) enumerates the console user's desktop through the session helper.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int?` | no | `null` | Process id. |
| `processName` | `string` | no | `null` | Process name, with or without .exe. |
| `titleRegex` | `string` | no | `null` | .NET regular expression matched against window titles (case-insensitive). |
| `className` | `string` | no | `null` | Window class name (case-insensitive). |
| `includeHidden` | `bool` | no | `false` | Include hidden windows. Cloaked (virtual-desktop) windows are still listed when visible. |
| `includeChildren` | `bool` | no | `false` | Also enumerate child windows of each top-level window. Child titles are omitted. |
| `limit` | `int` | no | `500` | Maximum windows to return, 1 to 5000. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token when enumerating as the console user. |

## `make_directory`

Creates a directory, including missing parents, inside a root with Write access.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Directory path. |

## `move_path`

Moves or renames a file or directory. Both paths need Write access. Refuses to move a root or a directory containing a root.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source` | `string` | yes |  | Source path. |
| `destination` | `string` | yes |  | Destination path (the new name). |
| `overwrite` | `bool` | no | `false` | Replace an existing destination file. |

## `prepare_remote_debugger_computer`

Runs 'msvsmon /prepcomputer /private /quiet' as a job: configures the Windows firewall and permissions for remote debugging on private networks. It changes firewall rules; needs the service context (default). Returns the job id.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `version` | `string` | yes |  | '2022' or '2026'. |
| `arch` | `string` | no | `x64` | 'x64' (default), 'arm64' or 'x86'. |
| `context` | `string` | no | `service` | 'service' (default) or 'interactive'. |

## `query_event_log`

Queries a Windows event log channel or an .evtx file under a root with Read access. Use the structured filters or a raw XPath, not both. Results are capped by Vestigo:EventLog:MaxEvents. Messages come from the installed publisher and are null when it is missing.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `channel` | `string` | yes |  | Channel name such as Application or System, or a path ending in .evtx. |
| `providers` | `string[]` | no | `null` | Provider names to match. |
| `levels` | `string[]` | no | `null` | Levels to match: critical, error, warning, information, verbose. |
| `eventIds` | `int[]` | no | `null` | Event IDs to match. |
| `since` | `string` | no | `null` | Only events at or after this ISO 8601 timestamp. |
| `until` | `string` | no | `null` | Only events at or before this ISO 8601 timestamp. |
| `messageContains` | `string` | no | `null` | Only events whose rendered message contains this text (case-insensitive). |
| `maxEvents` | `int?` | no | `null` | Maximum events to return. Clamped to MaxEvents. |
| `newestFirst` | `bool` | no | `true` | Return the newest events first. |
| `xpath` | `string` | no | `null` | Raw Event Log XPath, for example *[System[EventID=1000]]. Cannot be combined with the structured filters. |
| `includeXml` | `bool` | no | `false` | Include the event XML. |

## `read_capture`

Reads a page of decoded NDJSON events from a capture spool. To follow a live capture, call repeatedly with afterSequence set to the previous nextSequence and waitSeconds > 0 (long-poll); stop when completed is true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |
| `afterSequence` | `long` | no | `0` | Return events after this sequence. Use 0 to start at the beginning. |
| `limit` | `int?` | no | `null` | Maximum events to return. The server clamps this to its configured batch size. |
| `waitSeconds` | `double` | no | `0` | When no events are available yet, wait up to this many seconds for new ones. Clamped to the server's MaxReadWait. |

## `read_debug_output`

Reads a page of NDJSON OutputDebugString events from a capture spool. Each line is {receivedAt, time, pid, processName, message}. To follow a live capture, call repeatedly with afterSequence set to the previous nextSequence and waitSeconds > 0 (long-poll); stop when completed is true. Read-time filters skip non-matching lines and still advance nextSequence past every scanned line.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |
| `afterSequence` | `long` | no | `0` | Return events after this sequence. Use 0 to start at the beginning. |
| `limit` | `int?` | no | `null` | Maximum matching events to return. The server clamps this to its configured batch size. |
| `waitSeconds` | `double` | no | `0` | When no unread events are available yet, wait up to this many seconds for new ones. Clamped to the server's MaxReadWait. |
| `messageRegex` | `string` | no | `null` | Optional .NET regex (NonBacktracking, ignore-case) that the message must match. |
| `excludeRegex` | `string` | no | `null` | Optional .NET regex (NonBacktracking, ignore-case) that excludes a matching message. |
| `processIds` | `int[]` | no | `null` | When set, only messages from these process IDs are returned. |
| `processNames` | `string[]` | no | `null` | When set, only messages whose process name matches one of these simple globs (* and ?) are returned. |

## `read_job_output`

Reads a page of text output from a job. Pass the returned NextOffset to continue.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |
| `offset` | `long` | no | `0` | Byte offset to read from. Use 0 for the beginning. |
| `limit` | `int?` | no | `null` | Maximum bytes to return. Clamped to MaxJobReadBytes. |

## `read_text_file_since`

Returns complete lines after a cursor from tail_text_file or an earlier call. If the file shrank or was replaced, reading restarts at the beginning and rotated is true. An unterminated last line is held back until it is completed. Continue while hasMore is true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `offset` | `long` | yes |  | Cursor offset returned by the previous call. Use 0 to start at the beginning. |
| `identity` | `long` | no | `0` | Cursor identity returned by the previous call. Omit or 0 to skip the replacement check. |
| `maxLines` | `int?` | no | `null` | Maximum lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | no | `null` | Maximum bytes to examine. Clamped to MaxTailBytes. |

## `remove_class_filter`

Removes a driver service from a device class's upper or lower filter list.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `classGuid` | `string` | yes |  | Device class GUID. |
| `serviceName` | `string` | yes |  | Filter driver service name. |
| `position` | `string` | yes |  | upper or lower. |

## `remove_device_node`

Removes a device node by hardware ID and class GUID (nefcon --remove-device-node).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `hardwareId` | `string` | yes |  | Hardware ID of the node. |
| `classGuid` | `string` | yes |  | Device class GUID. |

## `remove_driver_store_package`

Removes driver store packages by original INF file name (for example mydriver.inf). Optional class GUID and service name narrow the match. nefcon offers no all-versions switch here.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infName` | `string` | yes |  | Original INF base name, no directory, for example mydriver.inf. |
| `classGuid` | `string` | no | `null` | Optional device class GUID to narrow the match. |
| `serviceName` | `string` | no | `null` | Optional driver service name to narrow the match. |

## `restart_device`

Restarts a device (remove and re-enumerate) by instance ID, for example after installing a filter driver.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID, for example ROOT\SYSTEM\0001. |

## `restart_service`

Stops a service (waiting for Stopped) and starts it again (waiting for Running). Dependents stopped by stopDependents are not restarted.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name. |
| `stopDependents` | `bool` | no | `false` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | no | `30` | Seconds to wait for each phase, 1 to 300. |

## `run_process`

Starts a process as a job. path is a file under a root with Execute access (name:relative\path or absolute), or a bare name such as cmd.exe when AllowPathLookup is enabled. .ps1 files run through pwsh.exe, else powershell.exe. context 'service' runs as the Vestigo service account; 'interactive' runs as the console user on winsta0\default (elevated: true uses the linked administrator token). stdout and stderr go to the job output. wait > 0 waits that many seconds and returns the output tail; wait 0 returns the job id immediately.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Executable or script path, or a bare name resolved from PATH. |
| `args` | `string[]` | no | `null` | Command-line arguments, one array element per argument. |
| `workingDirectory` | `string` | no | `null` | Working directory under a root with Read access. Defaults to the executable's directory. |
| `env` | `Dictionary<string, string>` | no | `null` | Environment variable overrides. |
| `context` | `string` | no | `service` | 'service' (default) or 'interactive'. |
| `elevated` | `bool` | no | `false` | Interactive only: run with the elevated linked token. |
| `wait` | `int` | no | `0` | Seconds to wait (0 to 300) before returning. 0 returns the job id immediately. |

## `run_scheduled_task`

Starts a task now. It runs as the task's own principal (often SYSTEM), not as the Vestigo service account. Requires Vestigo:ScheduledTasks:AllowChanges. wait > 0 waits that many seconds for it to finish and returns the last result.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |
| `arguments` | `string[]` | no | `null` | Optional parameters passed to the task's actions (replace $(Arg0), $(Arg1), ...). |
| `wait` | `int` | no | `0` | Seconds to wait (0 to 300) before returning. 0 returns immediately. |

## `scan_etl_metadata`

Lists distinct PDB identities referenced by one or more ETL files.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `etlPaths` | `string[]` | yes |  | ETL paths inside an allowed input root. |

## `set_default_audio_endpoint`

Sets the default audio playback or recording endpoint. role 'default' is Windows' Default Device (console and multimedia). 'communications' is the Default Communication Device. 'all' sets both. The flow follows from the endpoint id. Requires Vestigo:Audio:AllowSetDefault. The result lists endpoints of the same flow so the new defaults can be confirmed.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Endpoint id from list_audio_endpoints. |
| `role` | `string` | no | `default` | 'default' (Default Device), 'communications' (Default Communication Device) or 'all'. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |

## `set_environment_variable`

Changes a persistent environment variable and broadcasts WM_SETTINGCHANGE. Machine scope is a machine-wide configuration change. Names in Vestigo:Environment:ProtectedNames are refused. Append and prepend treat the value as ';'-separated entries and skip entries already present. The value is not echoed back.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `scope` | `string` | yes |  | Scope: machine, user, or process. Process only affects the Vestigo service and its children. |
| `name` | `string` | yes |  | Variable name. |
| `operation` | `string` | yes |  | Operation: set, unset, append, or prepend. |
| `value` | `string` | no | `null` | Value or list entries. Ignored for unset. |
| `expand` | `bool` | no | `false` | Write REG_EXPAND_SZ so %VAR% references are expanded by readers. List operations keep an existing REG_EXPAND_SZ kind. |

## `set_service_start_type`

Sets the start type: boot, system, auto, demand or disabled. boot and system are only valid for drivers.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name. |
| `startType` | `string` | yes |  | boot, system, auto, demand or disabled. |

## `set_test_signing`

Sets BCDE_LIBRARY_TYPE_ALLOW_PRERELEASE_SIGNATURES on the default boot entry. Requires Vestigo:System:AllowBootConfigurationChanges. Refused with secure_boot_enabled when turning test signing on unless Secure Boot is known to be disabled. Does not reboot; the running kernel changes only after a restart. Never logs secrets.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `enabled` | `bool` | yes |  | true to enable test signing, false to disable it. |

## `set_whql_developer_test_mode`

Sets the kernel WHQL developer test mode policy that allows traditional cross-signed drivers. Requires Vestigo:System:AllowBootConfigurationChanges. Availability depends on the Windows build. Does not reboot.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `enabled` | `bool` | yes |  | true to enable WHQL developer test mode, false to disable it. |

## `start_capture`

Starts a managed realtime WPP capture. Events are spooled locally; use read_capture (with waitSeconds to follow live) to read them.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pdbPaths` | `string[]` | no | `null` | PDB paths whose WPP control GUIDs should be enabled. |
| `tmfPaths` | `string[]` | no | `null` | TMF paths used only for message decoding. |
| `providerIds` | `string[]` | no | `null` | Additional ETW provider GUIDs. |
| `level` | `string` | no | `Verbose` | Trace level: None, Critical, Error, Warning, Information, or Verbose. |
| `matchAnyKeyword` | `string` | no | `null` | Match-any keyword bitmask. Omit for all keywords. |
| `matchAllKeyword` | `string` | no | `null` | Match-all keyword bitmask. Omit for no match-all filter. |
| `duration` | `string` | no | `null` | Maximum capture duration, for example 00:05:00. Capped by server configuration. |

## `start_debug_capture`

Starts the single DebugViewConsole capture of OutputDebugString messages. Events are spooled locally; use read_debug_output (with waitSeconds to follow live) to read them. A successful launch returns Running or Failed and does not guarantee the capture is ready; another DebugView instance can still fail the capture with dbwin_in_use after launch. A second start fails with capture_busy until the running capture is stopped.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `includeMessages` | `string[]` | no | `null` | Substring include filters passed to DebugViewConsole -i. Case-insensitive. Omit to capture every message. |
| `excludeMessages` | `string[]` | no | `null` | Substring exclude filters passed to DebugViewConsole -e. Case-insensitive. |
| `includeProcesses` | `string[]` | no | `null` | Process-name include filters passed to DebugViewConsole --include-process. |
| `excludeProcesses` | `string[]` | no | `null` | Process-name exclude filters passed to DebugViewConsole --exclude-process. |
| `duration` | `string` | no | `null` | Maximum capture duration, for example 00:05:00. Capped by Vestigo:DebugOutput:MaxDuration. |

## `start_remote_debugger`

Starts msvsmon.exe with /nostatus /silent /nofirewallwarn, /port, /timeout and the chosen authentication. Default context is 'interactive' (the console user's desktop), so attach rights match that user. auth 'windows' (default) authenticates callers; allowUsers adds /allow entries. auth 'none' disables authentication completely and requires iUnderstandNoAuthIsInsecure: true. Refuses a second instance on the same port. Connect Visual Studio to <host>:<port> afterwards.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `version` | `string` | yes |  | '2022' or '2026'. |
| `arch` | `string` | no | `x64` | 'x64' (default), 'arm64' or 'x86'. |
| `port` | `int?` | no | `null` | TCP port; defaults to Vestigo:RemoteDebugger:DefaultPort (4026). |
| `timeoutSeconds` | `int` | no | `3600` | Seconds msvsmon waits without a connection before exiting, 30 to 86400. |
| `auth` | `string` | no | `windows` | 'windows' (default) or 'none'. |
| `iUnderstandNoAuthIsInsecure` | `bool` | no | `false` | Required to be true when auth is 'none'. Acknowledges that anyone reaching the port can run code. |
| `allowUsers` | `string[]` | no | `null` | Windows users allowed to connect (DOMAIN\user or user@domain), each passed as /allow. Only with auth 'windows'. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: run with the elevated linked token so elevated processes can be debugged. |

## `start_service`

Starts a service or driver and waits until it is running. Starting an unsigned driver may fail unless test-signing is enabled.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name. |
| `arguments` | `string[]` | no | `null` | Optional start arguments passed to the service. |
| `timeoutSeconds` | `int` | no | `30` | Seconds to wait for Running, 0 to 300. 0 returns without waiting. |

## `stat_path`

Returns metadata for a file or directory: size, times, optional SHA-256, PE version info and Authenticode status for exe/dll/sys files.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Path as root:relative\path or an absolute path inside a root with Read access. |
| `computeSha256` | `bool` | no | `false` | Compute the SHA-256 of a file. |

## `stop_capture`

Stops a capture. For managed captures this also stops the ETW session; for attached captures it only detaches.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |

## `stop_debug_capture`

Stops the DebugViewConsole capture. Sends Ctrl+C, then kills the process tree if it does not exit.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `captureId` | `string` | yes |  | Capture identifier. |

## `stop_remote_debugger`

Stops msvsmon.exe by pid, or every instance started by Vestigo (all: true). Tries a graceful close, waits gracefulTimeoutSeconds, then kills the process. Only msvsmon.exe processes are accepted.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int?` | no | `null` | Process id of an msvsmon.exe. |
| `all` | `bool` | no | `false` | Stop all instances started by Vestigo. |
| `gracefulTimeoutSeconds` | `int` | no | `5` | Seconds to wait after the graceful close before killing, 0 to 120. |

## `stop_scheduled_task`

Stops all running instances of a task. Requires Vestigo:ScheduledTasks:AllowChanges.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Task path such as \Vendor\Nightly. |

## `stop_service`

Stops a service or driver and waits until it is stopped.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name. |
| `stopDependents` | `bool` | no | `false` | Also stop running dependent services first. |
| `timeoutSeconds` | `int` | no | `30` | Seconds to wait for Stopped, 0 to 300. 0 returns without waiting. |

## `tail_text_file`

Returns the last lines of a text file under a root with Read access. The file is opened with shared access so active logs can be read. Encoding is detected from a UTF-8 or UTF-16 byte order mark, otherwise UTF-8. Returns a cursor for read_text_file_since.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `lines` | `int` | no | `100` | Number of lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | no | `null` | Maximum bytes to examine from the end of the file. Clamped to MaxTailBytes. |

## `take_screenshot`

Captures a PNG of the console user's desktop. target 'desktop' (default) is the virtual screen across all monitors; 'display' is one GDI display (display='primary' or \\.\DISPLAYn from list_displays); 'window' is one top-level window (windowHandle from list_windows, or pid/processName/titleRegex that must match exactly one visible window); 'region' is x,y,width,height in the same physical-pixel coordinates list_displays reports. Returns a downscaled preview as image content (maxEdge, default 1920; 0 omits it) plus JSON metadata. saveTo stores the full-resolution PNG in a root with Write access (root:relative\file.png) and adds a download link (one-time ticket when API tokens are configured). Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | `string` | no | `desktop` | 'desktop' (default), 'display', 'window' or 'region'. |
| `display` | `string` | no | `null` | GDI device name such as \\.\DISPLAY2, or 'primary'. Required for target 'display'. |
| `windowHandle` | `string` | no | `null` | Hex window handle from list_windows (0x...). |
| `pid` | `int?` | no | `null` | Process id of the window to capture. |
| `processName` | `string` | no | `null` | Process name, with or without .exe. |
| `titleRegex` | `string` | no | `null` | .NET regular expression matched against window titles (case-insensitive). |
| `x` | `int?` | no | `null` | Region left, virtual-screen physical pixels. |
| `y` | `int?` | no | `null` | Region top, virtual-screen physical pixels. |
| `width` | `int?` | no | `null` | Region width in physical pixels. |
| `height` | `int?` | no | `null` | Region height in physical pixels. |
| `includeCursor` | `bool` | no | `false` | Draw the mouse cursor on the capture. |
| `maxEdge` | `int` | no | `1920` | Longest edge of the inline preview in pixels. 0 omits the preview. Default 1920. |
| `saveTo` | `string` | no | `null` | Optional destination as root:relative\file.png. Needs Write access. |
| `overwrite` | `bool` | no | `false` | Replace an existing saveTo file. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token when capturing as the console user. |

## `uninstall_driver`

Uninstalls the driver described by an INF inside a readable root (nefcon --uninstall-driver).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `infPath` | `string` | yes |  | INF path inside an allowed root. |

## `uninstall_filter_driver`

Removes a class filter and its driver service. With infPath it also purges that driver store package; allVersions then purges every version of it.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `classGuid` | `string` | yes |  | Device class GUID the filter is registered in. |
| `serviceName` | `string` | yes |  | Filter driver service name. |
| `position` | `string` | yes |  | upper or lower. |
| `infPath` | `string` | no | `null` | Optional INF path inside an allowed root whose driver store package is also removed. |
| `allVersions` | `bool` | no | `false` | Remove all versions of the driver store package. Needs infPath. |

## `uninstall_software`

Silently uninstalls a machine-scope program as a job. MSI uses msiexec /x /qn; EXE installers use QuietUninstallString or known switches for Burn, Inno Setup and NSIS; other installers are refused. Requires Vestigo:Software:AllowUninstall. dryRun returns the planned command only. wait > 0 waits that many seconds and returns the outcome.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Entry id from list_installed_software. |
| `dryRun` | `bool` | no | `false` | Return the planned command without running it. |
| `wait` | `int` | no | `0` | Seconds to wait (0 to 300) before returning. 0 returns the job immediately. |

## `validate_decoding_sources`

Loads PDB and TMF files and reports whether they can decode WPP messages.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `symbolPaths` | `string[]` | yes |  | PDB and TMF paths inside an allowed input root. |

## `wait_job`

Waits up to the given number of seconds for a job to finish and returns its state.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |
| `timeoutSeconds` | `int` | no | `30` | Maximum seconds to wait, 0 to 300. |

