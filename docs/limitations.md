# Limitations

Vestigo is a Windows lab server. The notes below are the bounds of each tool and module. How to call a tool is in [MCP tools](tools.md). Gates, tokens, and path confinement are in [security](security.md).

## Platform and transport

- Windows only. The NT Kernel Logger and kernel-mode realtime providers are not supported.
- Vestigo decodes events. It does not record a raw ETL file.
- There is no server push. Follow a live capture by long-polling `read_capture` or `read_debug_output` with `waitSeconds`. An external session can be read with `attach_etw_session` once `Vestigo:AttachableSessions` lists it.
- `/` explains MCP connection setup. `/health` is always anonymous. `/mcp` and `/files` are anonymous only while `ApiTokens` is empty.
- Traffic is plain HTTP. Use a trusted network or a TLS-terminating proxy for remote MCP traffic. Binding beyond loopback without tokens logs a startup warning.
- File-transfer tools return short-lived, single-use `/files` URLs. `take_screenshot` with `share: true` returns a separate 10-minute, single-use `/desktop/shares` URL for the downscaled preview. That URL is the credential, and an API token does not replace it. The MCP image block is for the model and may not appear in chat, so give the share URL to the person who needs to open or copy the image.

## Local configuration

`/config` edits settings and can restart the installed service, and only for a direct loopback client. Local users can read credentials and change privileged settings without an API token. Never proxy the configuration page or its Blazor transport. See [configuration](configuration.md#local-configuration-page).

## Elevation and session context

Reading an existing ETL file does not by itself require elevation. Creating a realtime session, controlling services, installing drivers, launching processes, and silent uninstall generally do.

`close_app` (default), `take_screenshot`, `inspect_screen`, `find_on_screen`, `send_input`, Desktop and Audio calls with `context: interactive`, and `run_process` with `context: interactive` need the service to run as SYSTEM with a user logged on at the console. Desktop and Audio calls and `close_app` with `context: service` run in the service session and do not need a console user.

## Tracing

- WPP enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` remain numeric in the upstream decoder.
- Symbol resolution uses the released [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW) package. A remote agent can resolve PDBs from an installed binary, the local cache, a configured symbol server, or a file uploaded into the built-in `traces` folder.

## Debug output

Debug-output capture needs an exclusive `DBWIN_BUFFER` reader. Another DebugView instance fails the start. Debugger-attached processes and kernel `DbgPrint` are not captured.

## Desktop, displays, and managed desktops

- Screenshots on the lock screen and the UAC secure desktop fail with `desktop_unavailable`. DRM, `SetWindowDisplayAffinity`, and some GPU or fullscreen-exclusive windows may capture black.
- `inspect_window` reports each failed Win32 read on that field and omits geometry when per-monitor DPI awareness v2 cannot be entered.
- Window-event capture does not include child processes. WinEvent does not report its own dropped events. A client disconnect does not stop a capture.
- `set_window_client_rect` reports `placement_not_achieved` when Windows or the application changes the requested client rectangle. Saved placement tokens do not survive a service restart.
- `snapshot_display_layout` includes inactive CCD paths. EDID manufacturer and product codes appear only when Windows marks the ids valid, and the raw EDID block is not returned. Restore compares active paths. A client disconnect does not restore a layout; the lease does. Another program can change the displays while a transaction is open. Real display-mutation tests require `VESTIGO_DISPLAY_LAYOUT_TESTS=1` on a dedicated machine.
- Display-layout transactions do not create virtual displays. `get_virtual_display_backend` reports that no released Vestigo backend component is selected, names that prerequisite, and `create_virtual_display` does not change the topology. A temporary virtual display, and a workflow that makes one the primary display, stay blocked until a released backend is selected.
- `observe_presentation` decodes ETW in a child process so a metadata fault does not terminate the service. It reports composed, independent-flip, or exclusive only from an explicit DXGI, DWM, or DxgKrnl event, and returns unknown when that evidence is missing, uncorrelated, dropped, truncated, or ambiguous. It does not infer exclusive fullscreen from window styles or monitor-sized bounds.
- Managed test desktops stay off until `Desktop:AllowDesktopSessions` is true. They use names Vestigo assigns, refuse the lock screen and UAC, and restore the recorded input desktop before stopping only processes launched into that session. Closing a desktop handle does not destroy a desktop Windows still considers in use. Listing and window inspection can target an inactive test desktop. Input, the cursor, the foreground window, and a full-desktop capture require `activate_desktop_session` and do not activate it themselves. `send_input` reports injection and an observed response separately.

## Screen perception and input

- `inspect_screen` finds elements through UI Automation and Windows OCR. OCR needs an installed OCR language for the user, and owner-drawn or game surfaces fall back to coarse OpenCV regions without names. The `captureId` that links `inspect_screen` to `send_input` expires after about two minutes, and the server keeps at most eight captures.
- `send_input` cannot reach the secure desktop (UAC prompts, the lock screen, Ctrl+Alt+Del) and cannot send the secure attention sequence. Input into elevated windows needs `elevated: true` because of User Interface Privilege Isolation, and Windows can still drop it silently.
- `requireFocus` stops the batch when the requested window is not foreground, but Windows can still change focus between that check and `SendInput`, and a click still lands on the window under the pointer.
- `injection.timing` is the helper's submission interval, not the job or observation interval. `observeMode` `pixels` compares pixels only and cannot see changes that do not alter the captured pixels, and an `observeCrop*` region only sees pixels inside it.
- `VESTIGO_OBSERVE_REUSE_HELPER=1` is an experimental process switch, not a configuration setting. One `send_input` call with `observe: true` then keeps a single inspection helper for that call, rechecks the desktop, window, and crop on every capture, and closes the helper when the call ends. If that helper fails to start, the call still submits the input and reports the observation as inconclusive. It does not fall back to a one-shot inspection for that call. Unsetting the variable makes later calls use one-shot inspection again. It does not retry a call whose input was already accepted. A console session change after the helper starts makes the observation inconclusive instead of `no-response`, because the later capture is still the original session. A failed inspection is not recovered by sending the input again.
- Dead keys and IME composition are not typed. `inputMode` `keys` sends virtual keys from the receiving window's layout and stops if that window's layout or the Caps Lock toggle visible to the helper changes. A `keys` text action is translated when it starts, so earlier actions in the same call may already have been submitted, and a mid-action stop is a tool error without `injection` or `timing`.

## Diagnostics

PE metadata uses [PeNet](https://github.com/secana/PeNet). The Diagnostics module requires Jobs.

- A valid Authenticode result is not a decision that Windows will load the file as a kernel driver. `explain_driver_load_failure` cites observed service, signature, and Code Integrity evidence and does not assert a cause. HVCI is the DeviceGuard scenario value. Windows lockdown mode is the S-mode value from `WldpQueryWindowsLockdownMode`, not a WDAC policy inventory.
- `list_process_modules` is a best-effort snapshot and returns root aliases only, never absolute paths.
- `list_kernel_dumps` returns file names and a small header prefix for the fixed kernel-dump locations. It does not return dump bytes or bugcheck parameters. `stage_kernel_dump` stays off until `Diagnostics:AllowKernelDumpStaging` is true. It copies the dump selected by that id into a write root and can issue a one-time download ticket. The tool result does not contain dump bytes.
- `list_crash_reports` returns Windows Error Reporting metadata only, not dump bytes. `capture_process_dump` stays off until `Diagnostics:AllowProcessDumps` is true, refuses protected processes, and does not upload the dump or create a download ticket.
- `probe_capture_support` only reports whether frames arrived. `backend` `wgc` is unsupported: this build does not implement Windows Graphics Capture.
- `list_process_connections` returns bindings for one process and omits remote addresses unless requested. `test_endpoint` stays off until `Diagnostics:AllowNetworkProbes` is true and the destination is allow-listed; it does not capture packets. A UDP send does not prove the peer is reachable. `stun: true` reports one STUN binding response and does not treat a timeout as a firewall block. QUIC is not supported.
- `list_video_encoders` is a Media Foundation `MFTEnumEx` enumeration, not a vendor-SDK inventory and not proof that an encoder can process a frame. `list_camera_formats` does not read camera samples. `probe_audio_format` does not emit sound unless `Diagnostics:AllowAudioPlayback` is true.
- `get_firewall_profiles` and `list_firewall_rules` stay off until `Diagnostics:AllowFirewallInventory` is true. They return stored profile flags and raw rule expressions. A stored action is not a verdict that traffic was blocked.
- `get_driver_verifier_state` reads `verifier /query` and `verifier /querysettings`. An active statistics dump is returned as text and is not parsed into flags. `set_driver_verifier` stays off until `Diagnostics:AllowDriverVerifier` is true and names only standard flags for specific `.sys` files. `reset_driver_verifier` stays available. Those tools do not reboot; use `reboot_machine` when `System:AllowReboot` is true.

## Devices and HID

`inspect_hid_device` returns capabilities, usages, and identity strings for interfaces it can open. It does not return a raw report descriptor, input reports, or the process that holds an exclusive handle. `list_hid_usages` is a bounded top-level usage scan.

## System, logon, and power

- `get_boot_state` reports uptime, an approximate UTC boot time, the Fast Startup configuration, safe mode, and a pending file-rename count. `fastStartupEnabled` does not prove the previous boot was hybrid, and the rename count does not include paths. Vestigo does not report a boot identity.
- BCD test-signing applies only to the default boot entry, takes effect after a reboot, and is refused while Secure Boot is on or its state is unknown.
- Turning UAC off (`set_uac` `disabled`) takes effect after a reboot and breaks UWP and Store apps until UAC is turned back on.
- `list_logon_sessions` reports who is logged on and which session interactive tools would use. Client names and addresses are omitted unless `includeClientAddresses` is true, and they are not written to the log. Listener sessions are omitted unless requested. There is no logoff or session-connect tool. Interactive tools that need a console user add `reason` (`nobody_logged_on`, `only_disconnected_sessions`, `session_0_only`, or `console_session_has_no_token`) to `no_interactive_session` or `user_not_available`.
- `reboot_machine` and `cancel_reboot` stay off until `System:AllowReboot` is true. Other tools report `rebootRequired` or schedule reboot-time file work and do not restart the machine themselves. Immediate reboot drops the MCP session. `force` closes applications without prompting.
- `request_autologon_enable` and `disable_autologon` stay off until `System:AllowAutoLogonChanges` is true. The Windows password is collected through a one-time browser consent URL and stored as an LSA secret. It is never a tool argument, never logged, and never written to `appsettings.json`.
- `request_interactive_logon`, `lock_workstation`, and `request_unlock_workstation` stay off until `System:AllowInteractiveLogon` is true. `request_interactive_logon` and `request_unlock_workstation` also need the MSI Interactive logon feature to have registered the Credential Provider (the DLL is staged by the `Server` feature); `lock_workstation` does not. Consent URLs must be opened in a user-facing desktop browser. `lock_workstation` locks the console session. `request_unlock_workstation` unlocks a locked console (omitting `userName` uses the locked user). Sign-in and unlock send the password only to LogonUI over a SYSTEM-only named pipe.
- `get_power_state` and `get_sleep_configuration` are read-only. Battery charging is separate from AC status. Last sleep and wake times are approximate. A missing sleep-policy index is null, not `doNothing`. `get_last_wake_source` reports the newest Power-Troubleshooter resume event and does not translate the wake-source number. Vestigo does not list power-request owners or change a power scheme.

## Logs, JSON, and registry

- `search_text` runs the bundled ripgrep with a bounded result count, line length, run time, and concurrency, so a search can stop early with `truncated` or `timedOut` set. It does not follow links or junctions, skips binary files, and decodes only UTF-8 and UTF-16 with a byte order mark. It looks at files as they are on disk and cannot follow a growing file; use `read_text_file_since` for that.
- `read_json_path` and `get_json_summary` read one UTF-8 JSON document under a Read root. Queries are parsed by [JsonPath.Net](https://github.com/json-everything/json-everything) and limited to names, indexes, and one trailing wildcard, so filters and recursive descent are rejected. A document above `Logs:MaxJsonBytes` is refused before it is read, and matched values are not logged. Line-oriented NDJSON belongs to `search_text`.
- `read_registry_value` reads one allow-listed key. The 32-bit and 64-bit views are explicit, `HKCU` is the console user, and a denied prefix wins. A registry symbolic link is followed only when its target passes the same checks. There is no write tool.

## Software, scheduled tasks, and audio

- Silent uninstall covers MSI, Burn, Inno Setup, NSIS, and entries that publish a `QuietUninstallString`. Other installers are refused, per-user entries cannot be uninstalled, and AppX/MSIX packages are not listed.
- Scheduled task tools work on the local machine only. They cannot create or edit tasks, and task history needs the `Microsoft-Windows-TaskScheduler/Operational` channel to be enabled.
- Audio default-device switching uses the undocumented `IPolicyConfig` interface and may fail on a future Windows release. Per-app device overrides are not shown or changed. The peak meter is a short sample, not a continuous monitor. The service context does not see user audio sessions.

## Windows servicing and self-update

Windows servicing diagnostics do not install updates, repair Windows, clear queues, or reboot. Windows Update Agent searches are offline-only. Native timeout or cancellation stops waiting without terminating Windows work. Log tails require Read roots. DISM package inventory generally requires elevation. See [Windows servicing diagnostics](windows-servicing.md).

`check_for_updates` uses the service account's proxy settings. `start_self_update` needs the MSI Automatic updates feature. After Vicius hands off to its temporary copy, the install result is only in the `%ProgramData%\...\Vestigo\updates` log until the service is back.

## External plugins

External plugin loading and module activation are disabled by default and require explicit administrative opt-in. Installation and updates require a service restart. Optional trusted .NET 10 plugins can add tools to the existing MCP endpoint and HTTP routes. Plugin routes require an API token only when `ApiTokens` is set. With empty `ApiTokens`, they are anonymous, as `/mcp` is. They run in-process with the service's privileges. Assembly dependency isolation is not a security sandbox. The public MSI contains no private plugins.

API 1.1 adds optional DI-registered tool providers with host-owned prefix validation, schema and result preservation, and explicit authenticated application session identities. API 1.0 plugins remain supported. Providers own backend resources, leases, and cleanup. This host bundles no Playwright plugin, browser, or interactive launcher.

See [plugin development and deployment](plugins.md).
