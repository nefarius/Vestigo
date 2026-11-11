# Troubleshooting

## Installer says .NET Runtime 10 and ASP.NET Core 10 are required

The MSI is framework-dependent and needs the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 (compatible 10.0.x patches; the highest Microsoft.NETCore.App patch must be at least the highest Microsoft.AspNetCore.App patch). The ASP.NET Core 10.0 Hosting Bundle also satisfies this. A later major (11 or newer) does not. Install them from [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) and run the installer again. In a silent install the same message is written to the MSI log.

From an elevated prompt:

```powershell
winget install --id Microsoft.DotNet.Runtime.10 --architecture x64
winget install --id Microsoft.DotNet.AspNetCore.10 --architecture x64
```

`--architecture x64` matters on Windows 11 ARM64, because a native ARM64 runtime does not satisfy the MSI.

On Windows 11 ARM64, the native `dotnet` command lists ARM64 runtimes under `%ProgramFiles%\dotnet`. The x64 runtimes are a separate install and appear under `%ProgramFiles%\dotnet\x64`. Confirm them with:

```powershell
& "$env:ProgramFiles\dotnet\x64\dotnet.exe" --list-runtimes
```

The MSI first asks WiX to load the x64 hostfxr. When that check misses the side-by-side x64 layout, Windows 11 ARM64 is still accepted when Microsoft's x64 ASP.NET 10 and .NET Runtime 10 shared-framework registry markers are both present, and the highest NETCore 10.0 patch is at least the highest ASP.NET 10.0 patch. A native ARM64 runtime alone does not satisfy the installer.

## Service fails to start with error 1920 and "You must install or update .NET"

The MSI installed, but `Vestigo.exe` could not find `Microsoft.NETCore.App` 10.0 (x64). On Windows 11 ARM64 this happens when only the x64 ASP.NET Core Runtime is present. Confirm both runtimes with:

```powershell
& "$env:ProgramFiles\dotnet\x64\dotnet.exe" --list-runtimes
```

Install the x64 .NET Runtime 10.0 (or the Hosting Bundle) from [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) and start the service again.

## `/mcp` returns 401

`ApiTokens` is configured and the request did not include a matching bearer token. `/health` remains anonymous. The log records the authentication failure without the token value.

## `/files` returns 401 `invalid_ticket`

The URL's `ticket` is unknown, expired (`Vestigo:Files:TicketLifetime`), already used by a completed transfer, in use by another request, issued for a different method or path, or lost in a service restart. Ask the tool (`get_download_url`, `get_upload_url`, `take_screenshot`, `convert_etl`) for a fresh link. An API token in `Authorization: Bearer` also works.

## `/desktop/shares` returns 401 `invalid_ticket`

The screenshot share ticket is missing, unknown, expired (10 minutes), already used, in use by another request, bound to a different screenshot id, or lost in a service restart. Call `take_screenshot` with `share: true` again and give the new URL to the person who needs the image. Do not fetch it first: one completed response consumes it. An API token does not authorize this URL. `share_unavailable` from the tool means `maxEdge` was 0, the preview was larger than 32 MiB, or every share slot was already in use. When `saveTo` stored the PNG and the link could not be issued, the tool succeeds and `shareError` reports that failure instead.

## Startup fails immediately

The exception names the setting. Common causes:

- `ListenAddress` is not an IP address, `Kestrel:Endpoints` is set, or `ListenPort` is outside 1-65535.
- A token, attachable-session, root name, or protected-name entry is blank.
- A size, count, or duration limit is not positive, or `MaxReadWait` is outside 0-2 minutes.
- A named root is missing an absolute path, has a duplicate name, has no access flags, or uses a generated volume-root name (`drive-c`, …) while `VolumeRoots` is not `None`.
- `VolumeRoots` is set and `VolumeRootAccess` has no flags.
- `Vestigo:Modules:<Name>` names an unknown module, or an enabled module depends on a disabled one (`Processes`, `Software`, and `Diagnostics` need `Jobs`; `RemoteDebugger` needs `Jobs` and `Processes`).
- `Devices:NefconPath`, `RemoteDebugger:ExtraSearchPaths`, or another module-specific setting failed validation.

`Refusing to use the Vestigo state directory: ...` means the state tree under `%ProgramData%\Nefarius Software Solutions\Vestigo` failed verification: a path component is a link or junction, a directory has an untrusted owner, or a parent can be modified by non-administrators. Remove the link, or take ownership as Administrators and fix the permissions, or reinstall the MSI (it re-applies the protected ACL), then restart the service.

A startup `SECURITY WARNING` is not a failure. A non-loopback bind without `ApiTokens` means anyone who can reach the port on the network can use `/mcp`. A volume-root warning means `VolumeRoots` granted Write or Execute on whole volumes. A privileged-module warning can also appear on loopback: without `ApiTokens`, any local client can use enabled `Processes`, `Devices`, or `RemoteDebugger`. `Services` is included only when the server binds beyond loopback, `Software:AllowUninstall` only when the Software module is also enabled, `ScheduledTasks:AllowChanges` only when the ScheduledTasks module is also enabled, `System:AllowBootConfigurationChanges` or `System:AllowUacChanges` only when the System module is also enabled, `Updates:AllowSelfUpdate` only when the Updates module is also enabled, `Desktop` when that module is enabled (window titles from the console user's desktop; screenshots only when `Desktop:AllowScreenshots` is also on), `Audio` when that module is enabled (audio devices and the processes using them; changing defaults only when `Audio:AllowSetDefault` is also on), `DebugOutput` when that module is enabled (it can read `OutputDebugString` from every session), and `Diagnostics` when that module is enabled (PE metadata, signature checks, loaded modules, SetupAPI install history, device problem codes, graphics adapters, TCP and UDP bindings owned by a process, Media Foundation video encoders, camera formats, audio format checks, and Windows Error Reporting report metadata; online revocation only when `Diagnostics:AllowOnlineRevocation` is also on; capture probes may observe whether frames arrive, and they do not return pixels, only when `Diagnostics:AllowCaptureProbes` is also on; network probes may contact allow-listed destinations, and they do not capture packets, only when `Diagnostics:AllowNetworkProbes` is also on; audio playback probes may play a synthetic tone, only when `Diagnostics:AllowAudioPlayback` is also on; process dumps may write a minidump into a Write root, only when `Diagnostics:AllowProcessDumps` is also on).

## The server is not reachable from another machine

`ListenAddress` must be `0.0.0.0` or `::` (or the machine address), and the inbound firewall rule `Vestigo MCP Server` must exist for the port. The MSI adds it for the "any address" modes. Restart the service after editing `appsettings.json`. A startup `SECURITY WARNING` in the log means no `ApiTokens` are set while the server is reachable from the network.

## Paths are rejected

Trace tools use the effective `AllowedInputRoots` and `AllowedOutputRoots` (the built-in `traces` folder when a list is empty). `outside_root` means the canonical path is outside those lists. `unsupported_extension` means the file is not `.etl`, `.pdb`, `.tmf`, `.zip`, `.json`, or `.ndjson` for the operation that received it. `already_exists` means the output is present and `overwrite` is false. `no_codeview` means the binary has no CodeView (RSDS) debug directory. `symbols_unresolved` means a PDB identity could not be found in the cache, the trace roots, or the symbol server. `symbol_auth_failed` means the symbol server returned 401 or 403; check `Vestigo:SymbolServerAuth`. `archive_invalid`, `archive_too_large`, and `unsafe_entry` come from `extract_trace_archive`.

File, log, process, and device tools use named `Vestigo:Roots` plus any `VolumeRoots`. `unknown_root` means the `name:` prefix is not configured. `access_denied` means that root lacks the `Read`, `Write`, or `Execute` flag the operation needs, or Windows denied the file. `roots_not_configured` means both `Vestigo:Roots` and `VolumeRoots` produced no roots. Per-user mapped network drives are not visible to the LocalSystem service.

## Realtime capture fails

`elevation_required` means the process could not create an ETW session. Run the service elevated. `session_exists` means that session name is already active; restart Vestigo so it can stop its own orphan, or stop the named session deliberately. `providers_required` means the call had neither provider GUIDs nor a PDB with WPP control GUIDs. `no_codeview`, `symbols_unresolved`, and `symbol_auth_failed` mean `binaryPaths` could not be turned into PDBs; the capture is not started.

## Presentation observation

`invalid_request` means the process id was not positive, the window handle was not positive, or the duration was not between 1 millisecond and 30 seconds, also capped by `MaxCaptureDuration`. `presentation_busy` means another `observe_presentation` call is still running.

A successful call can still return `mode` `unknown`. `providers_unavailable` means no provider was enabled, or at least one of DXGI, DWM, and DxgKrnl failed to enable. `events_dropped` means an event or the session reported lost events or lost buffers, including an event named `RTLostEvent` or `RTLostBuffer`. `events_truncated` means the 2000-event bound was hit. `decode_failed` means at least one event could not be decoded, so the result is not a mode. `no_events` means nothing was decoded. `no_correlation` means events were seen but none matched the process, or the requested window handle. `ambiguous` means more than one mode matched. `insufficient_evidence` means correlated events did not name a mode. `session_unavailable` means the ETW session could not be created. `cancelled` means the call was cancelled.

## Attaching to a session fails

`attach_not_allowed` means the name is not matched by `Vestigo:AttachableSessions`. `session_not_found` means no running session has that name (see `list_etw_sessions`). `invalid_request` for a `Vestigo-` name means that session is owned by a capture; use `read_capture` on it. `attach_failed`, or a capture that ends `Failed`, usually means the session is not a realtime session or the process lacks permission to consume it.

## Health is degraded or unhealthy

A default install reports Healthy: empty trace-root lists use the built-in `traces` folder. Unhealthy means the spool directory or, when either list uses that default, the `traces` folder is not writable.

## Interactive-session tools fail

`close_app`, `list_windows`, `list_displays`, `inspect_screen`, `find_on_screen`, `send_input`, `list_audio_endpoints`, and `set_default_audio_endpoint` default to `context: interactive` (the console user's session through the session helper). `context: service` runs those tools in-process on the service desktop and does not need a console user. `take_screenshot` is interactive only. `run_process` defaults to `service`; only `context: interactive` needs a console user.

`no_interactive_session` means nobody is logged on at the console. The failure includes `reason`: `nobody_logged_on` (no console session), `only_disconnected_sessions` (every other session is disconnected), `session_0_only` (only the services session, or the console session is session 0 for tools that refuse it), or `console_session_has_no_token` (the console session exists but Windows has no user token, such as the sign-in screen). `probe_capture_support`, `probe_audio_format`, and user-scope environment keep `user_not_available` and include that `reason`. `token_unavailable` and an access-denied console-user lookup do not. `list_logon_sessions` shows which sessions exist, which one is the console, and which one interactive tools would use (`isCurrentInput`). `get_logon_session` uses `invalid_request` for a negative session id, `session_not_found` when that id is absent, `access_denied` when Windows refuses the query, and `wts_unavailable` when Terminal Services does not return a session list. `probe_capture_support` reports that same condition as `user_not_available`. `probes_disabled` means `Vestigo:Diagnostics:AllowCaptureProbes` is false. `unsupported` from that tool means `backend` is `wgc`, which this build does not implement. `network_probes_disabled` means `Vestigo:Diagnostics:AllowNetworkProbes` is false. `destination_not_allowed` means the host, or the host and port, is not listed in `Vestigo:Diagnostics:AllowedProbeDestinations`. `audio_playback_disabled` means `probe_audio_format` was called with `playback: true` while `Vestigo:Diagnostics:AllowAudioPlayback` is false. `ambiguous_device` means more than one camera has that friendly name. `device_busy` means the camera or audio endpoint is in use or held in exclusive mode. `device_invalidated` means the audio endpoint was unplugged during the probe. `unsupported` from `list_video_encoders` means `backend` is not `media-foundation`. `media_failed` means `list_video_encoders` or `list_camera_formats` hit an unexpected Media Foundation failure, including a camera list larger than expected. `audio_failed` means `list_audio_endpoints` or `probe_audio_format` hit an unmapped WASAPI failure. `token_unavailable` means the console-user token could not be opened (the service needs SYSTEM). `elevation_unavailable` means `elevated: true` was requested and the console user has no linked administrator token. `desktop_unavailable` means the lock screen or UAC secure desktop, no displays are attached, `GetDC` failed, or the desktop blit failed. A failed `PrintWindow` is not enough: capture falls back to `BitBlt`. `helper_timeout` means the session helper did not finish in time. `helper_failed` means it exited without a usable JSON result, or a screenshot PNG could not be decoded.

`screenshots_disabled` means `Vestigo:Desktop:AllowScreenshots` is false; `inspect_screen`, `find_on_screen` and `send_input` with `observe` need it too. `window_placement_disabled` means `Vestigo:Desktop:AllowWindowPlacement` is false. `display_layout_disabled` means `Vestigo:Desktop:AllowDisplayLayoutChanges` is false. `virtual_display_backend_unavailable` means `create_virtual_display` was called and no released backend is selected, so the topology was not changed. `virtual_display_checkpoint_unsupported` means `owned-virtual-displays.json` has an unknown version and was left in place. `virtual_display_session_not_found` means that session is unknown or already closed. `virtual_display_identity_conflict` means the backend reported a display that was already in the topology. Vestigo does not remove that display and does not change the checkpoint. `desktop_sessions_disabled` means `create_desktop_session`, `activate_desktop_session`, `restore_desktop_session`, or `close_desktop_session` was called while `Vestigo:Desktop:AllowDesktopSessions` is false. `desktop_session_not_found` means the id is unknown or already closed. `desktop_session_limit` means `Desktop:MaxDesktopSessions` open sessions already exist. `desktop_checkpoint_unsupported` means `owned-desktops.json` has an unknown version and was left in place. `processes_still_running` means `close_desktop_session` used `fail-if-running` and an owned process is still alive; the input desktop was not changed. `input_desktop_changed` means restore found a different non-secure input desktop and did not switch. `desktop_host_lost` means the desktop-host helper exited. `secure_desktop_active` on a desktop-session call means the lock screen or a UAC prompt is showing, so nothing was switched. `unsupported_on_inactive_desktop` with reason `desktop_inactive` means the managed desktop is not the input desktop. Input, cursor, foreground, and a desktop, display, or region capture were not attempted and the desktop was not activated. Call `activate_desktop_session` first. Enumeration, window inspection, window-event capture, and a window capture stay available while the desktop is inactive. `desktopRetained` on a successful close means Windows kept the desktop because another handle or thread still uses it. `window_not_found` means the handle is not a window. `window_identity_mismatch` means the handle is not owned by `expectedPid`, or the window or process changed while it was being read. `process_identity_mismatch` means the process start time does not match `expectedStartTime`. `process_identity_unreadable` means the start time could not be read, so the expected start time was not checked. `dpi_context_unavailable` means per-monitor DPI awareness v2 could not be entered, so geometry was omitted. `dwm_unavailable` is a field error when DWM does not return extended frame bounds; the rest of the inspection can still succeed. `placement_not_achieved` means the measured client or normal rectangle does not match the request. `placement_not_found` means the restore token is unknown or expired. `placement_save_full` means 32 saved placements are already waiting. `display_transaction_busy` means a display transaction is already open. `display_config_rejected` means `SetDisplayConfig` refused the layout and nothing new was left applied by that call. `display_query_failed` means the active topology could not be read. A display observation with `timedOut: true` is a successful call that did not see the requested stable geometry; the transaction stays open. `untrusted_event` means one capture line was rejected and its contents were not stored. `too_many_captures` means `Desktop:MaxWindowEventCaptures` is already in use. `helper_timeout` means the window-event helper did not become ready. `cancelled` on a display transaction means the start call was cancelled and restore was attempted. `input_disabled` means `send_input` was called while `Vestigo:Input:AllowInput` is false (the Input module also needs Desktop enabled, or startup fails). `capture_expired` means a `captureId` is unknown or older than about two minutes, or was evicted because more than eight captures were made; run `inspect_screen` again. `capture_mismatch` means `diffAgainst` was taken with a different target, area or `maxEdge`. `element_not_found` means the capture has no element with that id. `secure_desktop_active` means a UAC prompt, the lock screen or Ctrl+Alt+Del is showing, where input is impossible. `uipi_blocked` means Windows refused the input because the foreground window is elevated; retry with `elevated: true`. `foreground_not_granted` in a `send_input` result means Windows would not bring `focusWindow` forward, so input went to the current foreground window. `vision_failed` means OpenCV could not process the image; `ocr_unavailable: ...` in an `inspect_screen` result means Windows OCR has no language pack for the user's languages (install one under Settings, Language) or the image exceeds the engine limit (capture a smaller target); `uia_unavailable: ...` means UI Automation could not start, and the element list then holds only OCR text and OpenCV regions. `set_default_disabled` means `Vestigo:Audio:AllowSetDefault` is false. `boot_changes_disabled` means `Vestigo:System:AllowBootConfigurationChanges` is false. `secure_boot_enabled` means `set_test_signing` tried to turn test signing on while Secure Boot is on or its state is unknown. `uac_changes_disabled` means `Vestigo:System:AllowUacChanges` is false. `invalid_level` means `set_uac` received a value other than `always`, `default`, `noDim`, `never`, or `disabled`. `uac_write_failed` means the UAC policy registry key could not be written.

## Process dumps fail

`capture_process_dump` runs as the Vestigo service and does not need a console user. `process_identity_mismatch` means `expectedStartTime` does not match the process that currently owns the pid. `process_dumps_disabled` means `capture_process_dump` was called while `Vestigo:Diagnostics:AllowProcessDumps` is false. `protected_process` means the pid is the Vestigo service, pid 4, a known critical process, or a protected process. `insufficient_disk_space` means the output volume cannot keep `Diagnostics:MinDumpFreeBytes` free after reserving `Diagnostics:MaxDumpBytes`. `disk_space_unknown` means that free space could not be read. `dump_too_large` means the dump exceeded `Diagnostics:MaxDumpBytes` and was deleted. `partial_copy` means the process memory could not be read completely and the partial file was deleted. `dump_failed` means the dump file could not be created or written. `cleanup_failed` on the following job line means that delete failed and the partial file remains; a retry uses `CREATE_NEW` and returns `already_exists` until the file is removed. `process_exited` means the process ended before the dump was written.

## Update check or self-update fails

`feed_unavailable` means `updates.json` or `updates.json.minisig` could not be downloaded from Buildbot (network, TLS, timeout, or HTTP error). The request uses the service account's proxy settings. `manifest_invalid` means the sidecar was missing or did not verify, the JSON could not be parsed, `manifestVersion` was missing, or a URL pointed outside `https://buildbot.nefarius.at/builds/Vestigo/latest/`. `self_update_disabled` means `Vestigo:Updates:AllowSelfUpdate` is false. `updater_not_installed` means `Nefarius_Vestigo_Updater.exe` or its `.json` is not next to `Vestigo.exe`; add the Automatic updates feature. `untrusted_executable` means Users can modify the updater file or a parent directory. `up_to_date` from `start_self_update` means the signed feed is not newer than the installed version. `update_in_progress` means a Vicius process is already running. `updater_failed` includes the Vicius exit code and the tail of `%ProgramData%\Nefarius Software Solutions\Vestigo\updates\<utc>.log`.

Vicius exit `202` means already up to date, `207` means the install was handed to the temporary copy (the service will restart), `104` is a feed or signature failure, `105` is product-detection failure, `106` is a busy user session (should not appear; Vestigo passes `--ignore-busy-state`), `107` is a download failure, `109` is a setup failure, and `116` is a checksum or Authenticode failure. After a `207` hand-off the original updater has exited; call `check_for_updates` once the service is back to see whether the installed version now matches the target.

## The updater does not offer a release

The installed configuration must remain at `%ProgramFiles%\Nefarius Software Solutions\Vestigo\Nefarius_Vestigo_Updater.json`. Missing updater files or the version registry value are expected when the `Updater` feature was deselected; use Modify to add Automatic updates, or reinstall without `REMOVE=Updater`. The registry value `HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version` must be the installed MSI product version. A manifest whose `manifestVersion` is older than the last one this user accepted is rejected. Exit code 104 means the manifest or its `.minisig` sidecar could not be verified. Exit code 116 means the downloaded MSI checksum or Authenticode publisher check failed.

## JSON document query fails

`read_json_path` and `get_json_summary` read one UTF-8 JSON document, for example a service snapshot `logs:state.json` containing `{"status":"pending","items":[{"name":"device"}]}`. `get_json_summary` reports the top-level names and types. `read_json_path` with `$.status` returns the string `pending`, and `$.items[*]` returns each array element. A line-oriented `.jsonl` file is not one document; search it with `search_text`.

`file_too_large` means the file is larger than `Vestigo:Logs:MaxJsonBytes`, or larger than a lower `maxBytes` passed by the caller. The file is not read. `value_too_large` means the one selected value exceeds `Vestigo:Logs:MaxJsonValueBytes`; the value is not returned. A wildcard can still succeed with `valueOmitted: true` on the matches that are too large. `invalid_query` means the query uses a filter, slice, union, recursive descent, or another unsupported form; the error names the supported subset. `query_no_match` means the query was valid and the name or index was absent. `invalid_json` means the file is not one JSON value, or an object repeats a property name. A syntax error gives the byte offset. A duplicate name is reported without that name. The error does not echo the file. `truncated` on a successful result means `Vestigo:Logs:MaxJsonMatches` stopped the match or member list early. Path errors match `tail_text_file`.

## Registry reads fail

`read_registry_value`, `list_registry_values`, and `get_registry_metadata` need the Registry module. `path_not_allowed` means the key is outside `Vestigo:Registry:AllowedPrefixes` (or the built-in locations when that list is empty). `path_denied` means the key is under a denied prefix, including `Winlogon`, `Lsa`, `SAM`, and `SECURITY`, even if an allow prefix also matches. `key_not_found` and `value_not_found` mean Windows has no such key or value. `access_denied` means the service account cannot open the key. `value_too_large` means the value is above `MaxBinaryBytes` or `MaxValueStringLength`; the value is not returned. `no_interactive_session` means an `HKCU` path was used and nobody is logged on at the console. The result includes the same `reason` as other interactive tools. `invalid_request` means the hive, view, or path shape is not supported. `truncated` on a list means `MaxEntries` or `MaxRecursionDepth` stopped the walk.

## Text search fails

`executable_missing` means `tools\rg.exe` is not next to Vestigo.exe and `Vestigo:Logs:RipgrepPath` is unset. Reinstall or stage the pinned release with `.\build.ps1 PrepareRipgrep`. `hash_mismatch` means the file's SHA-256 is not `Vestigo:Logs:RipgrepSha256`; set that value to empty only for a path you replaced on purpose. `invalid_pattern` carries ripgrep's regular-expression error; use `fixedStrings` to search for literal text. `invalid_request` means an empty or oversized pattern, a pattern with line breaks, or bad `globs` or `maxMatchesPerFile`. `search_busy` means `Vestigo:Logs:MaxConcurrentSearches` searches are already running; retry shortly. `search_failed` carries ripgrep's own error, for example a file or directory it could not open when nothing else matched. A result with `timedOut` or `truncated` set is not an error: narrow the pattern, path or globs. Path errors (`outside_root`, `not_found`, `access_denied`, `unknown_root`) come from the root checks and are the same as for `tail_text_file`.

## Binary inspection fails

`not_pe` means the file does not start as a PE image. `malformed_pe` means the headers could not be parsed. `file_too_large` means the file exceeds `Vestigo:Diagnostics:MaxInspectBytes`. `revocation_not_allowed` means `verify_file_signature` was called with `revocation: online` while `Vestigo:Diagnostics:AllowOnlineRevocation` is false. `invalid_request` on that tool means `revocation` was not `none`, `cacheOnly`, or `online`. `process_not_found`, `process_exited`, and `access_denied` come from `list_process_modules` when the pid is gone, exited during the snapshot, or cannot be read (including protected processes). `process_identity_mismatch` means `expectedStartTime` does not match the process; the pid may have been reused. A `partial` result is a snapshot that skipped some modules, not a failure. `get_device_install_history` returns `invalid_request` for an empty instance ID or a `limit` outside 1..200, `invalid_time` for a `since` value that is not ISO 8601, `access_denied` when Windows denies the log, and `io_error` when the log directory or a log file cannot be read. `truncated` means the byte cap skipped older log text. `explain_device_problem` returns `device_not_found` when the instance ID is not present, and `insufficientEvidence` (a field, not an error) when the problem code is outside the covered CM_PROB table. `not_a_file` from `inspect_binary` or `verify_file_signature` means the path is a directory.

## Debug-output capture fails

`executable_missing` means `tools\DebugViewConsole.exe` is not next to Vestigo.exe and `Vestigo:DebugOutput:ExecutablePath` is unset. Reinstall or stage the pinned asset with `.\build.ps1 PrepareDebugView`. `hash_mismatch` means the file's SHA-256 is not `Vestigo:DebugOutput:ExpectedSha256`; set that value to empty only for a path you replaced on purpose. `capture_busy` means another debug-output capture is still running; stop it first. `dbwin_in_use` means another DebugView, DebugView++, or similar reader already owns `DBWIN_BUFFER`. Close that viewer and retry. Vestigo never sends DebugViewConsole's `-x` flag, because that would stop every instance on the machine.

Messages from a process that has a debugger attached go to the debugger, not to this capture. Kernel `DbgPrint` is not captured. In session 0 the local and Global buffers are the same mapping.

## Scheduled task tools fail

`changes_disabled` means `Vestigo:ScheduledTasks:AllowChanges` is false; run, stop, enable, disable and delete need it. `protected_task` means delete or disable targeted a path under `Vestigo:ScheduledTasks:ProtectedPaths`. `history_disabled` means the `Microsoft-Windows-TaskScheduler/Operational` channel is off; enable it in Task Scheduler (Enable All Tasks History) or with `wevtutil sl Microsoft-Windows-TaskScheduler/Operational /e:true`. `task_disabled` means the task must be enabled before it can run, `already_running` that an instance is active, and `not_running` that there was nothing to stop. `invalid_path` means the task path is empty, is the root folder, or contains `..`, quotes, wildcards, or doubled backslashes. `not_found` means no task has that path; use `list_scheduled_tasks`. `access_denied` means the service account is not elevated.
## Decoded text is missing

`MissingFormatCount` counts WPP events whose message format was not in the loaded PDB or TMF sources. Point `binaryPaths` at the installed binary, add the matching PDB or TMF under `traces`, or enable symbol resolution for offline ETL. Enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` stay numeric. `symbol_auth_failed` means the configured symbol server rejected the request.


## The machine stops responding overnight

`get_power_state` reports whether the machine is on AC, whether a battery is present and charging, and approximate `lastSleepTimeUtc` and `lastWakeTimeUtc`. Those times move if the clock changes, and a zero interrupt-time counter is null. `capabilities.modernStandby` means the platform supports S0 low-power idle. `get_sleep_configuration` reports the lid-close action and the standby, hibernate, and monitor timeouts for AC and DC. A null lid action was not returned; it is not `doNothing`. A timeout of 0 means never. Vestigo does not list power-request owners and does not name the device that woke the machine. `power_api_failed` means the power read threw. A denied or missing policy index is a null field.

## A restart-required state never clears

`get_boot_state` reports `fastStartupEnabled` from `HiberbootEnabled`. When that value is true, shutting down can restore the previous kernel session, so driver and service state from before shutdown can still be there. Restart is the startup Windows treats as a full boot. The field is configuration only: it does not say the previous boot was hybrid, and Vestigo has no boot identity to compare across calls. `pendingFileRenameCount` is the queued rename and delete count. The paths are `list_pending_file_operations` when the Windows servicing module is enabled. `get_boot_security` `rebootPending` is only the BCD test-signing value versus the running kernel.

## Windows servicing diagnostics

- `servicing_busy`: a WUA, DISM or Restart Manager diagnostic still owns the
  single native-call slot. Wait for it to finish before retrying.
- `servicing_timeout`: the caller stopped waiting; native Windows work may still
  run and retain the slot. Repeated retries do not start additional workers.
- `native_diagnostic_failed`: inspect the preserved hexadecimal HRESULT and,
  when available, Win32 error number. `0x800702E4` means elevation is required;
  DISM online package enumeration normally needs an elevated service token.
- `invalid_request`: check state, offset and literal-filter limits in the tool
  catalog. WUA inventory accepts only pending or installed, with no arbitrary
  search expressions or online switch.
- `not_a_file`: `get_file_lockers` resolved a Read-root path that is not an
  existing file, for example a directory.

Per-source unknown/errors are not a clean bill of health. Log tails need Read
roots covering the fixed CBS/DISM paths; missing/disabled event channels can
return source errors. Failure evidence is bounded and does not prove causation.
See [Windows servicing diagnostics](windows-servicing.md) for the exact limits.