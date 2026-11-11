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

Attaches read-only to an existing realtime ETW session (not Vestigo-owned) allowed by Vestigo:AttachableSessions and decodes its events live. Read with read_capture; stop_capture only detaches and never stops the session. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sessionName` | `string` | yes |  | Exact name of a running ETW session, as listed by list_etw_sessions. |
| `symbolPaths` | `string[]` | no | `null` | Optional PDB and TMF paths inside an allowed input root used to decode WPP messages. |
| `binaryPaths` | `string[]` | no | `null` | Installed .sys, .dll, or .exe paths; their matching PDBs are resolved and treated as symbolPaths. |
| `duration` | `string` | no | `null` | Maximum attach duration, for example 00:05:00. Capped by server configuration. |

## `cancel_job`

Requests cancellation of a running job. Process jobs kill their process tree.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |

## `capture_process_dump`

Writes a minidump of one process as a job. Requires Vestigo:Diagnostics:AllowProcessDumps. output is a new .dmp file in a root with Write access. expectedStartTime, from list_processes, is required and is checked again immediately before the write. dumpType mini writes a small snapshot. dumpType full writes process memory and can contain credentials that were in the process. The dump runs as the Vestigo service. Protected processes and a fixed refusal list are rejected. The output volume must keep Vestigo:Diagnostics:MinDumpFreeBytes free after reserving Vestigo:Diagnostics:MaxDumpBytes. A dump over that cap is deleted. Vestigo does not upload the file and does not create a download ticket. wait > 0 waits that many seconds. The job output is a status line, never dump bytes.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int` | yes |  | Process id. |
| `output` | `string` | yes |  | New .dmp path in a root with Write access. |
| `expectedStartTime` | `DateTimeOffset` | yes |  | Start time from list_processes. A different process fails with process_identity_mismatch. |
| `dumpType` | `string` | no | `mini` | mini or full. full includes process memory. |
| `wait` | `int` | no | `0` | Seconds to wait (0 to 300) before returning. 0 returns the job immediately. |

## `check_for_updates`

Fetches the signed Buildbot updates.json, verifies updates.json.minisig with the Nefarius minisign public key, and compares the newest release to the installed Vestigo version (HKLM Version when the updater feature is installed, otherwise the entry assembly). Also reports whether the packaged updater is present, whether self-update is allowed, and the last start_self_update attempt.

No parameters.

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

Decodes ETL files to a JSON or NDJSON file inside an allowed output root and returns a summary, plus a download link when the output also lies in a Files root with Read access. See get_trace_settings for allowed roots.

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

## `explain_device_problem`

Explains the CM_PROB code of one device from observed evidence: device node, class filters, service state, boot security for code 52, Kernel-PnP configuration events, and the latest SetupAPI install sections. observed lists facts. explanation is the documented meaning of a covered code. hypotheses are labeled and cite that evidence. An uncovered code returns insufficientEvidence instead of a guess. This does not rank drivers.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID. |

## `extract_trace_archive`

Extracts .pdb, .tmf, and .etl entries from a zip inside an allowed input root into a directory inside the allowed output roots (the built-in traces folder when defaults are in use). Other entries are skipped. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `archivePath` | `string` | yes |  | Zip path inside an allowed input root. |
| `destination` | `string` | yes |  | Destination directory inside the allowed output roots. |
| `overwrite` | `bool` | no | `false` | Replace existing extracted files. |

## `fetch_file`

Imports an HTTP(S) URL or UNC file onto this host, into a root with Write access, as a background job. This does not export a screenshot or other file to the MCP client; use get_download_url or a tool-issued download or share link for that. Poll with wait_job and read_job_output; the output ends with the stored file's path, size and SHA-256.

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

## `find_on_screen`

Finds things on the console user's screen. text matches UI Automation names and OCR text (case-insensitive substring) across every collected candidate, before the 150-element cap, so a full UI Automation list cannot hide an OCR match. matchesTruncated is true when the matches themselves exceed that cap. template matches an image with multi-scale OpenCV template matching, from a PNG or JPEG at root:relative\file.png (needs Read access) or from a crop of an earlier capture (templateCaptureId with templateX, templateY, templateWidth, templateHeight in that capture's image pixels). Give text, a template, or both. Returns matches with boxes in image pixels and an elementId on the new captureId, so send_input can click a match. Template matches carry a confidence from 0 to 1. Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `text` | `string` | no | `null` | Text to find in element names or OCR text. |
| `template` | `string` | no | `null` | Template image as root:relative\file.png. |
| `templateCaptureId` | `string` | no | `null` | Earlier captureId to crop the template from. |
| `templateX` | `int?` | no | `null` | Crop left in the earlier capture's image pixels. |
| `templateY` | `int?` | no | `null` | Crop top. |
| `templateWidth` | `int?` | no | `null` | Crop width. |
| `templateHeight` | `int?` | no | `null` | Crop height. |
| `minConfidence` | `double` | no | `0.8` | Minimum template confidence 0.5 to 0.99. Default 0.8. |
| `target` | `string` | no | `desktop` | 'desktop' (default), 'display', 'window' or 'region'. |
| `display` | `string` | no | `null` | GDI device name such as \\.\DISPLAY2, or 'primary'. Required for target 'display'. |
| `windowHandle` | `string` | no | `null` | Hex window handle from list_windows (0x...). |
| `pid` | `int?` | no | `null` | Process id of the window to search. |
| `processName` | `string` | no | `null` | Process name, with or without .exe. |
| `titleRegex` | `string` | no | `null` | .NET regular expression matched against window titles (case-insensitive). |
| `x` | `int?` | no | `null` | Region left, virtual-screen physical pixels. |
| `y` | `int?` | no | `null` | Region top, virtual-screen physical pixels. |
| `width` | `int?` | no | `null` | Region width in physical pixels. |
| `height` | `int?` | no | `null` | Region height in physical pixels. |
| `imageMode` | `string` | no | `annotated` | 'annotated' (default), 'raw' or 'none'. |
| `maxEdge` | `int` | no | `1280` | Longest edge of the returned image in pixels, 320 to 3840. Default 1280. |
| `elevated` | `bool` | no | `false` | Use the elevated token when capturing as the console user. |

## `get_boot_security`

Returns UEFI vs legacy BIOS, Secure Boot, BCD test-signing (configured, takes effect at next boot), the running kernel test-signing state, WHQL developer test mode, and rebootPending when the BCD value and the running state differ. Notes cover Secure Boot, a pending reboot, and that BCD changes apply only to the default boot entry.

No parameters.

## `get_boot_state`

Returns uptime in whole seconds, an approximate UTC boot time (now minus GetTickCount64; a clock change moves it), whether Fast Startup is configured (HiberbootEnabled; null when absent, and not proof the previous boot was hybrid), safe mode (normal, safe, or network), and the pending file-rename pair count. Paths are not returned. A failed probe is a null field. Does not reboot.

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

## `get_device_install_history`

Reads %windir%\INF\setupapi.dev.log and rotated setupapi.dev.*.log files for sections whose header token equals instanceId (case-insensitive, whole token, not a substring or hardware ID). Returns title, start and end, exit status, original numeric codes, INF and driver-version lines, and the log file name. complete is false when a section is truncated. includeRaw adds the section text, capped per section; otherwise raw lines are omitted. The read stops at Vestigo:Diagnostics:MaxInstallHistoryBytes and prefers newer bytes, setting truncated when older text was skipped. This does not rank drivers.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `instanceId` | `string` | yes |  | Device instance ID, matched exactly against the section-header token. |
| `since` | `string` | no | `null` | ISO 8601 timestamp. Sections that end before this time are omitted. |
| `limit` | `int` | no | `20` | Maximum sections to return, newest kept, oldest first. 1 to 200. |
| `includeRaw` | `bool` | no | `false` | Include the section text. Defaults to false. |

## `get_display_layout_transaction`

Returns the open display transaction, including restore differences and whether restoration was verified.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | no | `null` | Transaction id. Omit to return the open transaction. |

## `get_download_url`

Returns an HTTP GET URL, not the file bytes, for downloading an existing file from a root with Read access, with curl and Invoke-WebRequest examples. When API tokens are configured the URL carries a one-time ticket that expires and stops working after one successful download, so the API token never has to be shared. For a screenshot the user can open immediately, call take_screenshot with share:true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File as root:relative\path or an absolute path inside a root with Read access. |

## `get_environment_variable`

Reads one environment variable. Machine scope reads the Session Manager registry key and user scope reads the environment of the user logged on at the console; both return the unexpanded value and whether it is REG_EXPAND_SZ. Fails with user_not_available when nobody is logged on.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `scope` | `string` | yes |  | Scope: machine, user, or process. |
| `name` | `string` | yes |  | Variable name. |

## `get_file_lockers`

Best-effort Restart Manager query for one existing file in a Read root. Returns process identities/start times, application/service names and reboot-reason flags. Never shuts down/restarts processes. Not a complete kernel handle or minifilter inventory; an empty list does not prove a file can be replaced.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path in a configured Read root, absolute or root:relative. |
| `limit` | `int` | no | `100` | Maximum processes, clamped to WindowsServicing:MaxResults. |

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

## `get_json_summary`

Returns the root type, SHA-256, and top-level shape of a UTF-8 JSON document under a root with Read access. Object properties include type and serialized size. Array elements are counted, not listed. Values are not written to the log.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `maxBytes` | `int?` | no | `null` | Optional byte cap, clamped down to Vestigo:Logs:MaxJsonBytes. The file is not read when it is larger. |

## `get_logon_session`

Returns one Windows logon session, including token elevation, local Administrators membership, and the logon id when the session token can be opened. Those token fields are null when the token cannot be read. includeClientAddresses adds the client name and address; otherwise those fields are null and are not written to the log.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sessionId` | `int` | yes |  | WTS session id from list_logon_sessions. |
| `includeClientAddresses` | `bool` | no | `false` | Include client name and address. Default false. These values are not written to the log. |

## `get_power_state`

Returns AC line status (online, offline, or unknown), whether a battery is present, its percent, whether it is charging (separate from AC status), remaining seconds, the active scheme GUID and name, approximate UTC last sleep and wake times, and platform capabilities. A battery that is absent leaves the battery fields null. Charging can be true while AC status is offline. Last sleep and wake are UTC now minus the interrupt-time counters, so a clock change moves them. fastStartupSupported is a capability, not the HiberbootEnabled configuration from get_boot_state. A failed probe is a null field. There is no write.

No parameters.

## `get_reboot_pending`

Collects independent 64-bit registry and WUA reboot signals. Returns pending, notPending, or unknown; unreadable sources are never treated as clear. Installer and CBS session presence are advisory activity signals. Does not reboot or change the queue.

No parameters.

## `get_registry_metadata`

Returns the last write time, subkey count, and value count of an allow-listed registry key. It does not read values or security descriptors.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Key path: HKLM, HKU, or HKCU, then a subkey. |
| `view` | `string` | no | `registry64` | registry64 or registry32. Default registry64. |

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

## `get_servicing_logs`

Returns bounded tails of the fixed CBS.log and DISM dism.log, using existing Read roots and text-tail decoding. Each source has its own error/truncation. Does not decompress rotated CBS archives or convert Windows Update ETLs. Raw logs may contain sensitive machine/user data. Optional literal filtering applies only within the tail window, not the whole file.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `contains` | `string` | no | `null` | Literal case-insensitive text, KB, update id or HRESULT; at most 128 characters. |

## `get_servicing_status`

Fast CBS registry snapshot plus pending.xml/reboot.xml metadata. No XML content is parsed and no DISM/SFC health scan or repair is invoked. Missing pending files do not establish component-store health.

No parameters.

## `get_sleep_configuration`

Returns the active scheme's AC and DC lid-close action (doNothing, sleep, hibernate, shutdown, or unknown), standby, hibernate, and monitor timeouts in seconds (0 means never), wake-timer enablement, and hybrid sleep. A value Windows did not return is null, not doNothing and not zero. There is no write.

No parameters.

## `get_system_info`

Returns the host Windows name, edition, display version (for example 24H2), version string, major/minor/build/UBR, whether it is Windows 11 or Server, whether the install was in-place upgraded, OS and processor bitness, ARM64, UAC disabled, and the machine name. One failed probe becomes a null field.

No parameters.

## `get_trace_settings`

Returns the effective Trace roots, the traces named root, symbol cache and server (userinfo and query string removed), authentication scheme without the secret, attachable-session patterns, and size limits. See this tool before uploading symbols or starting a capture.

No parameters.

## `get_uac`

Returns the host UAC policy: enabled (EnableLUA), the slider level (always, default, noDim, never, disabled, or custom), ConsentPromptBehaviorAdmin, ConsentPromptBehaviorUser, PromptOnSecureDesktop, and notes. Group Policy can override these values. EnableLUA changes apply after a reboot.

No parameters.

## `get_update_failure_details`

Collects evidence from a bounded newest WUA history window, four update/servicing/download event channels, and root-confined CBS/DISM log tails. since defaults to 24 hours ago; event/history timestamps are filtered, log tails are not time-parsed. contains is a literal KB/id/HRESULT/text filter. Source failures remain explicit. Empty results do not prove absence; no causal diagnosis is inferred from matching text.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `contains` | `string` | no | `null` | Literal KB, update id, HRESULT or text, at most 128 characters. |
| `since` | `DateTimeOffset?` | no | `null` | Earliest event/history time; defaults to 24 hours ago. Log lines retain raw timestamps. |
| `limit` | `int` | no | `50` | Maximum entries examined/returned per evidence source, clamped to WindowsServicing:MaxResults. |

## `get_update_history`

Queries a bounded newest-first WUA history window. Result codes 3, 4, 5 mean succeeded-with-errors, failed, aborted. failuresOnly filters within the examined window; advance offset by Examined, not returned count. HRESULTs retain unsigned hexadecimal form. History may have been reset and is not proof of installed-package state.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `offset` | `int` | no | `0` | Number of newest-first history entries to skip. |
| `limit` | `int` | no | `100` | Maximum entries examined; clamped to WindowsServicing:MaxResults. |
| `failuresOnly` | `bool` | no | `false` | Return only results with errors, failure or abortion in this window. |

## `get_update_storage_status`

Reports available space on the Windows volume and metadata for fixed update database/log/pending files. Does not recurse through caches, calculate WinSxS directory size, determine reserved storage, or perform cleanup. Free space is an observation, not a prediction of update space requirements.

No parameters.

## `get_upload_url`

Returns the HTTP PUT URL for uploading a file into a root, with curl and Invoke-WebRequest examples. Uploads need Write access to the root. When API tokens are configured the URL carries a one-time ticket bound to this path and these flags that expires and stops working after one successful upload, so the API token never has to be shared.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Destination as root:relative\path. |
| `overwrite` | `bool` | no | `false` | Replace an existing file. |
| `deferToReboot` | `bool` | no | `false` | Replace a locked file at the next reboot instead of renaming it aside. |

## `get_window_event_capture`

Returns the state, counts, dropped events, truncation, and whether new windows or child processes are included.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Capture id. |

## `get_windows_update_status`

Snapshots WUA reboot requirement, installer busy flag, registered update services and six Windows service states. Unavailable WUA data remains an explicit source error. Stopped demand/trigger-start services are not identified as blockers. Does not infer USO activity or trigger a scan.

No parameters.

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

## `inspect_binary`

Reads one PE file inside a root with Read access: architecture (x86, x64, arm64, armnt, arm64ec, arm64x), PE32 or PE32+, subsystem, selected DLL characteristics, timestamp, managed flag, CodeView PDB file name, version, size, and SHA-256. Non-PE input fails with not_pe; a broken PE fails with malformed_pe. Fields come from one open of the file. This does not verify a signature.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File as root:relative\path or an absolute path inside a root with Read access. |
| `computeSha256` | `bool` | no | `true` | Compute SHA-256. Defaults to true. |

## `inspect_screen`

Looks at the console user's screen and describes it for an agent. Returns an annotated image with numbered boxes plus a JSON element list (id, source uia|ocr|cv, role, name or text, box in image pixels) that works without seeing the image. Elements come from UI Automation, Windows OCR, and OpenCV contour regions where the first two find nothing. The result has a captureId: pass it to send_input to click an elementId, or to use x,y in the returned image's pixels. The id stays valid for about two minutes. diffAgainst compares with an earlier captureId (same target and maxEdge) and lists the regions that changed. imageMode 'annotated' (default), 'raw' or 'none' controls the image. grid:true draws a labelled coordinate grid instead of boxes. The list holds at most 150 elements, shared evenly by UI Automation and OCR, with OpenCV regions filling what remains. uia and ocr report how collection went (ok, skipped, truncated, or a failure). truncated, candidates and omitted report when collected elements were left out. Takes the same targets as take_screenshot. Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | `string` | no | `desktop` | 'desktop' (default), 'display', 'window' or 'region'. |
| `display` | `string` | no | `null` | GDI device name such as \\.\DISPLAY2, or 'primary'. Required for target 'display'. |
| `windowHandle` | `string` | no | `null` | Hex window handle from list_windows (0x...). |
| `pid` | `int?` | no | `null` | Process id of the window to inspect. |
| `processName` | `string` | no | `null` | Process name, with or without .exe. |
| `titleRegex` | `string` | no | `null` | .NET regular expression matched against window titles (case-insensitive). |
| `x` | `int?` | no | `null` | Region left, virtual-screen physical pixels. |
| `y` | `int?` | no | `null` | Region top, virtual-screen physical pixels. |
| `width` | `int?` | no | `null` | Region width in physical pixels. |
| `height` | `int?` | no | `null` | Region height in physical pixels. |
| `imageMode` | `string` | no | `annotated` | 'annotated' (default), 'raw' or 'none'. |
| `maxEdge` | `int` | no | `1280` | Longest edge of the returned image in pixels, 320 to 3840. Default 1280. |
| `grid` | `bool` | no | `false` | Draw a coordinate grid instead of numbered boxes. |
| `uia` | `bool` | no | `true` | Read UI Automation elements (names and roles; edit values are never read). Default true. |
| `ocr` | `bool` | no | `true` | Recognize text with Windows OCR. Default true. |
| `includeCursor` | `bool` | no | `false` | Draw the mouse cursor on the capture. |
| `diffAgainst` | `string` | no | `null` | captureId of an earlier inspection to diff against. |
| `elevated` | `bool` | no | `false` | Use the elevated token when capturing as the console user. |

## `inspect_window`

Reads one window in physical virtual-screen pixels. Returns the raw GetWindowRect, the DWM visible frame, and the client rectangle in screen coordinates, plus DPI, frame metrics, styles, visibility, owner relationships, display intersections, and process integrity, elevation, and LocalSystem state when those reads succeed. Each field has its own success or error. A failed read is not reported as a zero rectangle. Geometry is omitted when per-monitor DPI awareness v2 cannot be entered. expectedPid and expectedStartTime reject a reused handle or pid. context 'interactive' (default) reads the console user's desktop. This does not change the window.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `windowHandle` | `string` | yes |  | Hex window handle from list_windows (0x...). |
| `expectedPid` | `int?` | no | `null` | Process id from list_windows. A different owner fails with window_identity_mismatch. |
| `expectedStartTime` | `DateTimeOffset?` | no | `null` | Process start time from list_processes. A different process fails with process_identity_mismatch. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token. |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pdbPath` | `string` | yes |  | Absolute path, traces:relative path, or rooted path to a .pdb file. |

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

## `list_camera_formats`

Lists video capture devices, or the advertised media types of one device. Omit device to enumerate names and symbolic links without activating a source. Pass a symbolic link or friendly name to activate that source, read its advertised types, and shut it down. No camera sample is read. Some cameras may indicate activity while the source is open. stage is enumerated or advertised. processedSamples is false.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `device` | `string` | no | `null` | Symbolic link or friendly name. Omit to list devices without formats. |
| `limit` | `int` | no | `32` | Maximum devices when device is omitted, or maximum formats when one device is selected. Clamped to 1..Vestigo:Diagnostics:MaxCameraFormats. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_crash_reports`

Lists Windows Error Reporting ReportArchive and ReportQueue metadata from the fixed machine and user stores. Each row has the store, profile label, folder name, event type, event time, application name and path, report id, and signature values. Dump files beside a report are named with their length only. Dump bytes and raw Report.wer text are not returned. scope is machine, user, or all. since drops reports whose event time is missing or earlier. This is a metadata snapshot and does not explain the crash.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `scope` | `string` | no | `all` | machine, user, or all. |
| `since` | `DateTimeOffset?` | no | `null` | Drop reports earlier than this time, and reports with no event time. |
| `limit` | `int` | no | `50` | Maximum reports. Clamped to 1..Vestigo:Diagnostics:MaxCrashReports. |

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

Lists GDI displays (\\.\DISPLAYn) with bounds, work area, DPI, and the physical monitors currently driven by each source, including cloned outputs. includeModes adds refresh, active mode, rotation, scaling, the adapter LUID, and advanced-color state when Windows reports them. context 'interactive' (default) uses the console user's session.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `includeModes` | `bool` | no | `false` | Include refresh, active mode, rotation, scaling, adapter LUID, and advanced color. |

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

## `list_graphics_adapters`

Lists DXGI adapters in the service process, including software and remote adapters. Each adapter includes vendor, device, subsystem, and revision when they are non-zero, the LUID, video memory, the user-mode driver version when CheckInterfaceSupport returns one, and outputs with the adapter LUID they are attached to. A Display-class device node is matched by PCI ids and reported as matchConfidence exact, subsystem, device, or ambiguous. Unknown numbers are null. This does not rank drivers.

No parameters.

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

## `list_logon_sessions`

Lists Windows logon sessions. isCurrentInput marks the session interactive tools would use. includeClientAddresses adds the client name and address; otherwise those fields are null and are not written to the log. Station names are displayed, not interpreted. There is no logoff or connect tool.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `includeDisconnected` | `bool` | no | `true` | Include disconnected sessions. Default true. |
| `includeListeners` | `bool` | no | `false` | Include listener rows such as RDP-Tcp. Default false. |
| `includeClientAddresses` | `bool` | no | `false` | Include client name and address. Default false. These values are not written to the log. |

## `list_pending_file_operations`

Reads both Session Manager REG_MULTI_SZ rename/delete queues. Preserves raw paths, pair index and replacement marker; normalizes recognized NT DOS/UNC paths without accessing them. An unpaired source is malformed, never assumed deleted. Paths may disclose software/user names even outside configured roots; the module is opt-in. No files are opened or changed.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `limit` | `int` | no | `100` | Maximum pairs across both queues; clamped to WindowsServicing:MaxResults. |

## `list_process_connections`

Lists TCP endpoints and UDP bindings owned by one process. Each row has protocol, family, TCP state when known, and the local address and port. includeRemoteAddresses adds the remote address and port; otherwise those fields are null. A UDP row is a local binding and does not prove that the peer is reachable. No packet payloads, headers, or credentials are read. expectedStartTime, from list_processes, rejects a reused pid. The list is a best-effort snapshot.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int` | yes |  | Process id. |
| `limit` | `int` | no | `200` | Maximum rows to return. Clamped to 1..Vestigo:Diagnostics:MaxProcessConnections. |
| `includeRemoteAddresses` | `bool` | no | `false` | Include remote address and port. Local bindings are always returned. |
| `expectedStartTime` | `DateTimeOffset?` | no | `null` | Start time from list_processes. When set, a different process start time fails with process_identity_mismatch. |

## `list_process_modules`

Lists modules loaded in a process: name, size, architecture, and version. baseAddress is omitted when process memory is not readable. includePaths returns root:relative aliases for modules inside a Read root; other modules have path null and pathOutsideRoots true. Absolute paths are never returned. Hashing and signature checks are separate calls. expectedStartTime, from list_processes, rejects a reused pid. The list is a best-effort snapshot and can be partial.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pid` | `int` | yes |  | Process id. |
| `limit` | `int` | no | `200` | Maximum modules to return. Clamped to 1..Vestigo:Diagnostics:MaxProcessModules. |
| `includePaths` | `bool` | no | `false` | Return root aliases for modules inside a Read root. |
| `expectedStartTime` | `DateTimeOffset?` | no | `null` | Start time from list_processes. When set, a different process start time fails with process_identity_mismatch. |

## `list_processes`

Lists running processes with pid, name, session id and start time. Filters are optional.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `nameFilter` | `string` | no | `null` | Case-insensitive substring of the process name. |
| `pid` | `int?` | no | `null` | Only this process id. |
| `sessionId` | `int?` | no | `null` | Only this session id (0 is services, the console session is usually 1). |
| `limit` | `int` | no | `500` | Maximum rows, 1 to 2000. |

## `list_registry_values`

Lists values and subkey names under an allow-listed registry key. recurse walks nested keys up to MaxRecursionDepth. maxEntries can only lower MaxEntries. Denied children are omitted. Values are not written to the log.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Key path: HKLM, HKU, or HKCU, then a subkey. |
| `recurse` | `int` | no | `0` | Nested levels to open. Zero lists only this key. Clamped down to MaxRecursionDepth. |
| `maxEntries` | `int?` | no | `null` | Optional entry cap, clamped down to Vestigo:Registry:MaxEntries. |
| `view` | `string` | no | `registry64` | registry64 or registry32. Default registry64. |

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

## `list_servicing_packages`

Starts a read-only DISM API package inventory job against the running Windows image. read_job_output returns JSON with package names, documented state names/raw codes and release types. Usually requires elevation. No localized text parsing, arbitrary DISM arguments, health scan or repair. DISM may write its own log. Shares the single native-call slot with WUA.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `limit` | `int` | no | `200` | Maximum returned packages; clamped to WindowsServicing:MaxResults. Truncation is explicit. |

## `list_video_encoders`

Lists video encoders registered with Media Foundation, using MFTEnumEx. backend must be media-foundation. The result is an enumeration: transforms are not activated and no frames are submitted. It is not an inventory of vendor encoder APIs that do not register an MFT. discoverySource is MFTEnumEx, stage is enumerated, activated and processedSamples are false.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `backend` | `string` | no | `media-foundation` | Discovery backend. Only media-foundation is implemented. |
| `limit` | `int` | no | `100` | Maximum encoders. Clamped to 1..Vestigo:Diagnostics:MaxVideoEncoders. |

## `list_window_event_captures`

Lists window-event captures started by this service process.

No parameters.

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

## `list_windows_updates`

Starts an offline-only WUA applicability inventory job. Returns a Jobs id; read_job_output returns a JSON OperationResult with update identity/revision, title, KBs and states. WUA may perform expensive local work and write its own diagnostic/cache data. No download, install or online scan is requested. Cancellation/timeout stop waiting, not the native call. One native servicing call runs at a time.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `state` | `string` | no | `pending` | pending or installed. Installed WUA results are not a complete CBS package inventory. |
| `includeDrivers` | `bool` | no | `true` | Include driver/firmware updates. |
| `includeHidden` | `bool` | no | `false` | Include hidden updates. |
| `limit` | `int` | no | `100` | Maximum returned updates; clamped to WindowsServicing:MaxResults. |

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

## `observe_display_layout_transaction`

Samples the active topology until a target is active, matches the requested mode and position, and has the same geometry for stableSamples consecutive samples, or until timeoutSeconds. There is no fixed pixel tolerance. A timeout returns the last differences with timedOut true and leaves the transaction open. Omit the target fields to wait until the original topology is stable again.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Transaction id. |
| `timeoutSeconds` | `int` | no | `15` | Seconds to wait, from 1 to 120. |
| `stableSamples` | `int` | no | `2` | Consecutive matching samples required. |
| `monitorDevicePath` | `string` | no | `null` | Monitor device path from the snapshot. |
| `connectorInstance` | `uint?` | no | `null` | Connector instance from the snapshot. |
| `adapterLuid` | `string` | no | `null` | Adapter LUID from the snapshot, HHHHHHHH:LLLLLLLL. |
| `targetId` | `uint?` | no | `null` | CCD target id from the snapshot. |
| `x` | `int?` | no | `null` | Requested left, used when a target is selected. |
| `y` | `int?` | no | `null` | Requested top. |
| `width` | `int?` | no | `null` | Requested width. |
| `height` | `int?` | no | `null` | Requested height. |
| `rotation` | `string` | no | `null` | Optional IDENTITY, ROTATE90, ROTATE180, or ROTATE270. |

## `prepare_remote_debugger_computer`

Runs 'msvsmon /prepcomputer /private /quiet' as a job: configures the Windows firewall and permissions for remote debugging on private networks. It changes firewall rules; needs the service context (default). Returns the job id.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `version` | `string` | yes |  | '2022' or '2026'. |
| `arch` | `string` | no | `x64` | 'x64' (default), 'arm64' or 'x86'. |
| `context` | `string` | no | `service` | 'service' (default) or 'interactive'. |

## `probe_audio_format`

Checks whether a render or capture endpoint supports a shared-mode PCM format. format is pcm-rate-16-channels, for example pcm-48000-16-2. endpoint is an endpoint id or default-render. The format check does not start the stream and does not emit sound. playback true renders a 440 Hz tone for durationMs, only when Vestigo:Diagnostics:AllowAudioPlayback is true, and only on a render endpoint. The probe runs in the console user's session, uses shared mode, and does not change the default endpoint or record a microphone. A result names the format, duration, backend wasapi, and workload format-check or synthetic-tone. It is not an application performance measurement.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `endpoint` | `string` | yes |  | Endpoint id, or default-render. |
| `format` | `string` | yes |  | pcm-rate-16-channels. Rate is 8000..192000. Channels are 1 or 2. |
| `durationMs` | `int` | no | `1000` | Playback length in milliseconds. Ignored unless playback is true. Capped by Vestigo:Diagnostics:MaxAudioProbeDuration. |
| `playback` | `bool` | no | `false` | Render a synthetic tone. Requires Vestigo:Diagnostics:AllowAudioPlayback. |

## `probe_capture_support`

Observes whether frames arrive for one display in the console user's session. backend 'dxgi' uses Desktop Duplication through the session helper. backend 'wgc' is unsupported in this build. durationMs is capped by Vestigo:Diagnostics:MaxProbeDuration. The result reports the session, desktop name, what the API advertised, how many frames arrived, first-frame latency, and the final HRESULT. Frame arrival is an observation and proves no cause. No pixel data is returned. Requires Vestigo:Diagnostics:AllowCaptureProbes. Fails with user_not_available when nobody is logged on.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | `string` | no | `primary` | 'primary' or a GDI device name such as \\.\DISPLAY1. |
| `backend` | `string` | no | `dxgi` | 'dxgi' or 'wgc'. Only dxgi is implemented. |
| `durationMs` | `int` | no | `1000` | How long to wait for frames, in milliseconds. Capped by Vestigo:Diagnostics:MaxProbeDuration. |

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

## `read_json_path`

Reads one JSON value, or a bounded set of values, from a UTF-8 JSON document under a root with Read access. The query supports $.name, quoted keys, array indexes, and one trailing .* or [*] wildcard. It does not filter, slice, or walk descendants. Line-oriented NDJSON belongs to search_text. Matched values are not written to the log.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `query` | `string` | yes |  | Query such as $.status, $['dashed-key'], $.items[0].name, or $.items[*]. |
| `maxBytes` | `int?` | no | `null` | Optional byte cap, clamped down to Vestigo:Logs:MaxJsonBytes. The file is not read when it is larger. |

## `read_registry_value`

Reads one value from an allow-listed registry key. HKCU is the console user's hive. view is registry64 or registry32 and does not follow the service bitness. REG_BINARY is hashed only when it fits MaxBinaryBytes. The value is not written to the log.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | Key path: HKLM, HKU, or HKCU, then a subkey. HKLM and HKEY_LOCAL_MACHINE are the same hive. |
| `value` | `string` | no | `null` | Value name. Omit it to read the key's default value. |
| `expand` | `bool` | no | `false` | Expand a REG_EXPAND_SZ as the service account. Other types ignore this. |
| `includeBinary` | `bool` | no | `false` | Include REG_BINARY as hex. This cannot raise MaxBinaryBytes. |
| `view` | `string` | no | `registry64` | registry64 or registry32. Default registry64. |

## `read_text_file_since`

Returns complete lines after a cursor from tail_text_file or an earlier call. If the file shrank or was replaced, reading restarts at the beginning and rotated is true. An unterminated last line is held back until it is completed. Continue while hasMore is true.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `offset` | `long` | yes |  | Cursor offset returned by the previous call. Use 0 to start at the beginning. |
| `identity` | `long` | no | `0` | Cursor identity returned by the previous call. Omit or 0 to skip the replacement check. |
| `maxLines` | `int?` | no | `null` | Maximum lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | no | `null` | Maximum bytes to examine. Clamped to MaxTailBytes. |

## `read_window_event_capture`

Reads window-event records after afterSequence. waitSeconds long-polls for new records. Completed is true when no further records will arrive. Dropped is the helper queue count. Native WinEvent loss is not reported because Windows does not provide that count.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Capture id from start_window_event_capture. |
| `afterSequence` | `long` | no | `0` | Return records with a sequence greater than this value. |
| `limit` | `int?` | no | `null` | Maximum records, clamped to Vestigo:MaxReadBatch. |
| `waitSeconds` | `double` | no | `0` | Seconds to wait for new records, up to Vestigo:MaxReadWait. |

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

## `resolve_binary_symbols`

Reads the CodeView PDB identity from each .sys, .dll, or .exe under a Files root with Read access, then resolves it through the symbol cache, trace input roots, and the configured symbol server. Returns per-binary origin, local PDB path, and diagnostics.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `binaryPaths` | `string[]` | yes |  | Absolute or root:relative paths of .sys, .dll, or .exe files. |

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

## `restore_display_layout_transaction`

Applies the snapshot taken before the transaction and reads the topology again. RestorationVerified is true only when the fingerprint matches. A failed restore stays open and blocks another transaction. The restore is written to the display database so a temporary test layout cannot return at logoff. Requires Vestigo:Desktop:AllowDisplayLayoutChanges.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Transaction id. |

## `restore_window_placement`

Restores a placement saved by set_window_client_rect. The window and process identity are checked again. SetWindowPlacement can let Windows activate the window; Vestigo does not call SetForegroundWindow. Achieved is true only when the measured normal rectangle matches the saved one. Requires Vestigo:Desktop:AllowWindowPlacement.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `token` | `string` | yes |  | Token returned by set_window_client_rect. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token. |

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

Lists distinct PDB identities referenced by one or more ETL files. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `etlPaths` | `string[]` | yes |  | ETL paths inside an allowed input root. |

## `search_text`

Searches a text file, or every file under a directory, inside a root with Read access using the bundled ripgrep, and returns only the matching lines (plus optional context). Use it to filter large or numerous log and trace files on the machine instead of downloading or tailing them. The pattern is a Rust regular expression unless fixedStrings is true. Hidden files are searched, .gitignore rules are not applied, symbolic links and junctions are not followed, and binary files are skipped. UTF-8 and UTF-16 files with a byte order mark are decoded. Results are bounded by Vestigo:Logs (lines returned, context lines, line length, run time); truncated or timedOut is true when a bound stopped the search early, so narrow the pattern, path or globs and retry.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pattern` | `string` | yes |  | Regular expression (or literal text with fixedStrings) to look for. A line matches when any part of it matches. |
| `path` | `string` | yes |  | File or directory: root:relative\path or an absolute path inside a root. A directory is searched recursively. |
| `fixedStrings` | `bool` | no | `false` | Treat the pattern as literal text instead of a regular expression. |
| `ignoreCase` | `bool` | no | `false` | Match case-insensitively. Default is case-sensitive. |
| `wholeWord` | `bool` | no | `false` | Only match whole words. |
| `contextBefore` | `int` | no | `0` | Lines of context to return before each match. Clamped to Vestigo:Logs:MaxSearchContextLines. |
| `contextAfter` | `int` | no | `0` | Lines of context to return after each match. Clamped to Vestigo:Logs:MaxSearchContextLines. |
| `maxMatchesPerFile` | `int?` | no | `null` | Stop searching each file after this many matching lines. |
| `globs` | `string[]` | no | `null` | Optional file-name globs applied below a directory, for example *.log or !*.old.log. A leading ! excludes. Matched case-insensitively against the path. |

## `send_input`

Injects mouse and keyboard input as the console user. actions run in order: move, click (button, count), down, up, scroll (deltaY notches down, deltaX right), drag (x,y to toX,toY), type (text as Unicode characters), key (a key or chord such as enter, ctrl+c, alt+f4, win+r) and wait (milliseconds). A point is elementId from inspect_screen or find_on_screen, or x and y: in the pixels of the returned image when captureId is set, otherwise absolute virtual-screen physical pixels. focusWindow (hex handle from list_windows) is brought to the foreground first. observe:true waits observeDelayMs, then returns a fresh inspect_screen result, so one call does act and look; it needs Vestigo:Desktop:AllowScreenshots. Cannot reach the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) and cannot reach elevated windows unless elevated=true. Requires Vestigo:Input:AllowInput. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `actions` | `List<InputActionSpec>` | yes |  | Actions to perform in order. |
| `captureId` | `string` | no | `null` | captureId that element ids and x,y refer to. |
| `focusWindow` | `string` | no | `null` | Hex window handle from list_windows to focus first. |
| `observe` | `bool` | no | `false` | Return a fresh inspect_screen result after the actions. |
| `observeDelayMs` | `int` | no | `300` | Milliseconds to wait before observing, 0 to 5000. Default 300. |
| `observeWindowHandle` | `string` | no | `null` | Observe only: window handle to inspect instead of the whole desktop. |
| `imageMode` | `string` | no | `annotated` | Observe only: 'annotated' (default), 'raw' or 'none'. |
| `maxEdge` | `int` | no | `1280` | Observe only: longest edge of the returned image, 320 to 3840. |
| `elevated` | `bool` | no | `false` | Use the elevated token, needed to reach elevated windows. |

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

## `set_uac`

Sets the UAC slider level: always, default, noDim, never, or disabled. Requires Vestigo:System:AllowUacChanges. custom is not accepted. disabled writes only EnableLUA and needs a reboot; it also breaks UWP and Store apps until UAC is turned back on. Slider-only changes keep EnableLUA on. Does not reboot. Never logs secrets.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `level` | `string` | yes |  | UAC level: always, default, noDim, never, or disabled. |

## `set_whql_developer_test_mode`

Sets the kernel WHQL developer test mode policy that allows traditional cross-signed drivers. Requires Vestigo:System:AllowBootConfigurationChanges. Availability depends on the Windows build. Does not reboot.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `enabled` | `bool` | yes |  | true to enable WHQL developer test mode, false to disable it. |

## `set_window_client_rect`

Moves and sizes a window so its client area matches x,y,width,height in physical virtual-screen pixels. The call accounts for the window's current decorations and DPI, then measures the result. Achieved is true only when the measured client rectangle matches. Windows or the application may alter the request; that is reported instead of being treated as success. Activation and Z-order are preserved by SetWindowPos flags. savePlacement stores the previous placement for restore_window_placement. The token does not survive a service restart. Requires Vestigo:Desktop:AllowWindowPlacement. context service changes a window in the service session; interactive changes the console user's window and needs an elevated helper for an elevated window.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `windowHandle` | `string` | yes |  | Hex window handle from list_windows. |
| `x` | `int` | yes |  | Client left in physical screen pixels. |
| `y` | `int` | yes |  | Client top in physical screen pixels. |
| `width` | `int` | yes |  | Client width in physical screen pixels. |
| `height` | `int` | yes |  | Client height in physical screen pixels. |
| `coordinateSpace` | `string` | no | `physical-screen-pixels` | Must be physical-screen-pixels. |
| `expectedPid` | `int?` | no | `null` | Owner pid. A different owner fails before the window is moved. |
| `expectedStartTime` | `DateTimeOffset?` | no | `null` | Process start time. A different process fails before the window is moved. |
| `savePlacement` | `bool` | no | `false` | Save the current placement and return a restore token. |
| `context` | `string` | no | `interactive` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token. |

## `snapshot_display_layout`

Captures the active display topology. Each path is identified by its monitor device path, connector instance, adapter LUID, and target id. GDI device names such as \\.\DISPLAY1 are included as operational names and are not stable across hotplug. Coordinates are physical screen pixels. This does not change the displays. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `elevated` | `bool` | no | `false` | Use the elevated token. |

## `start_capture`

Starts a managed realtime WPP capture. Events are spooled locally; use read_capture (with waitSeconds to follow live) to read them. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pdbPaths` | `string[]` | no | `null` | PDB paths whose WPP control GUIDs should be enabled. |
| `tmfPaths` | `string[]` | no | `null` | TMF paths used only for message decoding. |
| `binaryPaths` | `string[]` | no | `null` | Installed .sys, .dll, or .exe paths; their matching PDBs are resolved and treated as pdbPaths. |
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

## `start_display_layout_transaction`

Validates and applies a layout for targets that are already active. The original topology is saved before the change. persistent defaults to false and is the only way the change is also written to the display database. Only one transaction can be open. Cancellation of this call attempts to restore the original layout. A later client disconnect does not, because MCP is stateless; the lease restores it. Service shutdown and the next service start also attempt restore. External display changes are reported and cannot be blocked. This tool does not create or remove virtual displays. Requires Vestigo:Desktop:AllowDisplayLayoutChanges.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `targets` | `List<DisplayTargetChange>` | yes |  | Active targets to reposition or change mode. Identity must match snapshot_display_layout. |
| `persistent` | `bool` | no | `false` | Write the change to the display database. Leave false for a temporary test layout. |
| `elevated` | `bool` | no | `false` | Use the elevated token. |

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

## `start_self_update`

Launches the packaged Vicius updater with --silent-update so it downloads and installs the signed Buildbot MSI without UI. Requires Vestigo:Updates:AllowSelfUpdate and the Automatic updates feature. The updater copies itself to TEMP and exits 207 before the MSI stops this service; the MCP session will drop. dryRun returns the planned command only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `dryRun` | `bool` | no | `false` | Return the planned updater command without running it. |

## `start_service`

Starts a service or driver and waits until it is running. Starting an unsigned driver may fail unless test-signing is enabled.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | yes |  | Service name. |
| `arguments` | `string[]` | no | `null` | Optional start arguments passed to the service. |
| `timeoutSeconds` | `int` | no | `30` | Seconds to wait for Running, 0 to 300. 0 returns without waiting. |

## `start_window_event_capture`

Captures window creation, destruction, show, hide, foreground, and location changes, plus exit of the selected process. A process filter includes windows that process creates later and does not include child processes. A window handle without a process filter follows only that window. Events are observed; geometry and DPI are sampled when an event arrives. DPI changes are comparisons of samples, not WM_DPICHANGED. Process exit has no inferred cause. WinEvent does not report how many events it dropped before delivery; dropped counts only the helper queue. The capture keeps running after this call returns. Read it with read_window_event_capture and stop it with stop_window_event_capture. A stateless client disconnect does not stop it. Service shutdown does. Interactive session only.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `windowHandle` | `string` | no | `null` | Hex window handle. Omit to follow a process. |
| `pid` | `int?` | no | `null` | Process id. Later windows from this process are included; child processes are not. |
| `expectedStartTime` | `DateTimeOffset?` | no | `null` | Start time from list_processes. Required to prove the pid was not reused when it can be read. |
| `durationSeconds` | `int` | no | `30` | Seconds to capture, from 1 to Vestigo:Desktop:MaxWindowEventDuration. |
| `elevated` | `bool` | no | `false` | Use the elevated token for the helper. |

## `stat_path`

Returns metadata for a file or directory: size, times, optional SHA-256, PE version info and Authenticode status for exe/dll/sys files. Signature status is Signed, Unsigned, Invalid, Untrusted, or Unavailable, with source embedded, catalog, or none. Catalog-signed files are not reported as Unsigned. Verification uses cache-only revocation and does not contact the network.

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

## `stop_window_event_capture`

Stops a window-event capture and unhooks its helper. Already-stopped captures return their final state.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `id` | `string` | yes |  | Capture id. |

## `tail_text_file`

Returns the last lines of a text file under a root with Read access. The file is opened with shared access so active logs can be read. Encoding is detected from a UTF-8 or UTF-16 byte order mark, otherwise UTF-8. Returns a cursor for read_text_file_since.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File path: root:relative\path or an absolute path inside a root. |
| `lines` | `int` | no | `100` | Number of lines to return. Clamped to MaxTailLines. |
| `maxBytes` | `int?` | no | `null` | Maximum bytes to examine from the end of the file. Clamped to MaxTailBytes. |

## `take_screenshot`

Captures a PNG of the console user's desktop. target 'desktop' (default) is the virtual screen across all monitors; 'display' is one GDI display (display='primary' or \\.\DISPLAYn from list_displays); 'window' is one top-level window (windowHandle from list_windows, or pid/processName/titleRegex that must match exactly one visible window); 'region' is x,y,width,height in the same physical-pixel coordinates list_displays reports. Returns a downscaled preview as MCP image content for the model (maxEdge, default 1920; 0 omits it) plus JSON metadata. That image block may not be shown to the user. share:true also returns a short-lived, single-use URL and resource link for the same preview so the user can open and copy it; present that URL and do not fetch it first. saveTo stores the full-resolution PNG in a root with Write access (root:relative\file.png) and adds a download link (one-time ticket when API tokens are configured). Do not use fetch_file to export a screenshot. Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.

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
| `share` | `bool` | no | `false` | When true, also return a short-lived single-use URL for the downscaled preview so a person can open and copy it. The MCP image block is for the model and may not appear in chat. Present the share URL and do not fetch it. Needs maxEdge above 0. |
| `elevated` | `bool` | no | `false` | Interactive only: use the elevated token when capturing as the console user. |

## `test_endpoint`

Observes one destination from Vestigo's own network context, which can differ from an application's routing, proxy, or credentials. protocol 'dns' resolves the host, 'tcp' also connects, 'tls' also performs a TLS handshake with certificate name and chain validation, and 'udp' sends one datagram. udp outcome 'sent' does not prove the peer is reachable: a blocked UDP port fails silently. stun true, only with udp, sends one STUN binding request and waits for a matching response. A missing response is not a firewall block. The mapped address is unauthenticated. quic returns protocol_unsupported. The direct probe contacts only the host and port given here, and only when that host, or that host and port, is listed in Vestigo:Diagnostics:AllowedProbeDestinations. TLS chain building may also contact Authority Information Access issuer servers. Revocation checks may contact revocation servers when Vestigo:Diagnostics:AllowOnlineRevocation is enabled. timeoutMs is capped by Vestigo:Diagnostics:MaxEndpointProbeDuration, and a udp call is also capped by Vestigo:Diagnostics:MaxUdpProbeDuration. A udp datagram is at most Vestigo:Diagnostics:MaxUdpPayloadBytes. includeResolvedAddress reports the addresses used. A TCP result is not a measure of media quality, jitter, or loss. No payloads, headers, or credentials are read. Requires Vestigo:Diagnostics:AllowNetworkProbes.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `host` | `string` | yes |  | Host name or IP address. Not a URL. |
| `port` | `int` | yes |  | Port, 1-65535. Ignored for protocol 'dns' except for the allow-list match. |
| `protocol` | `string` | no | `tcp` | 'dns', 'tcp', 'tls', or 'udp'. 'quic' is refused. |
| `timeoutMs` | `int` | no | `5000` | Budget in milliseconds. Capped by Vestigo:Diagnostics:MaxEndpointProbeDuration. UDP is also capped by MaxUdpProbeDuration. |
| `includeResolvedAddress` | `bool` | no | `false` | Include resolved addresses in the result. |
| `stun` | `bool` | no | `false` | With protocol udp, send one STUN binding request and wait for a matching response. |

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

Loads PDB and TMF files and reports whether they can decode WPP messages. See get_trace_settings for allowed roots.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `symbolPaths` | `string[]` | yes |  | PDB and TMF paths inside an allowed input root. |

## `verify_file_signature`

Checks the embedded Authenticode signature and, when the file has none, the Windows catalog signature. outcome is unsigned, valid, invalid, untrusted, or unavailable. source is embedded, catalog, or none. unavailable is not the same as unsigned. revocation is none, cacheOnly (default, no network), or online. online requires Vestigo:Diagnostics:AllowOnlineRevocation and may contact revocation servers. Signer fields are omitted unless includeSigner is true. A valid signature does not mean Windows will load the file as a kernel driver.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | yes |  | File as root:relative\path or an absolute path inside a root with Read access. |
| `revocation` | `string` | no | `cacheOnly` | none, cacheOnly (default), or online. |
| `includeSigner` | `bool` | no | `false` | Include signer subject, issuer, and thumbprint. |

## `wait_job`

Waits up to the given number of seconds for a job to finish and returns its state.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | yes |  | Job identifier. |
| `timeoutSeconds` | `int` | no | `30` | Maximum seconds to wait, 0 to 300. |

