---
title: QA
description: Test-rig workflows for a Vestigo lab PC.
---

# QA workflows

Treat the lab PC as a remote pair of hands. The agent installs the build, puts the machine in the state the case needs, captures evidence, and pulls the files back. [Features](features.md) lists every area. [Security](security.md) says which of these stay off until you opt in.

## Install and uninstall a build

1. `list_installed_software` / `get_installed_software` to see what is already there and whether a silent uninstall exists.
2. Drop the new MSI or setup package into a write root, or `fetch_file` it from an HTTP(S) URL or UNC share as a background job. For a fetch, poll `wait_job` until the job is no longer `Running`, and start `run_process` only if the state is `Succeeded`.
3. `run_process` (Processes is opt-in) to install. Use `wait` or `wait_job` / `read_job_output` to watch the exit code.
4. After the run, `uninstall_software` with `dryRun` first, then for real. Silent uninstall stays off until `Software:AllowUninstall` is true. MSI, Burn, Inno Setup, NSIS, and quiet-uninstall strings are supported. Per-user entries and AppX/MSIX packages are not.

## Toggle devices

Use the Devices module when the case needs a driver stack, a filter, or a device that is present or not.

1. `find_devices` / `get_device` to locate the instance ID.
2. `enable_device`, `disable_device`, or `restart_device`.
3. Install or uninstall a package with `install_driver` / `uninstall_driver` when the build under test is a driver. Reboot yourself when `rebootRequired` is true.

## Screenshot the failure

1. Enable Desktop. Screenshots stay off until `Desktop:AllowScreenshots` is true.
2. `list_windows` and `list_displays` to find the window or monitor.
3. `take_screenshot` of the desktop, one display, one window, or a region. The MCP image is for the agent and may not appear in chat. Pass `share: true` and give the person the returned URL; it shows the downscaled preview once and then expires. Pass `saveTo` in a write root when the full-resolution PNG must be kept; that result includes a one-time download link when tokens are configured. Do not use `fetch_file` to export the image.

The lock screen and UAC secure desktop fail with `desktop_unavailable`. A console user must be logged on.

## See whether the machine slept

1. `get_power_state` reports AC, battery charging, approximate last sleep and wake times, and whether the platform supports modern standby.
2. `get_sleep_configuration` reports the lid-close action and the standby timeout on AC and on battery. A null value was not returned. A timeout of 0 means never.
3. Fast Startup configuration is `get_boot_state` `fastStartupEnabled`. That setting does not prove the previous boot was hybrid.

## Why a screenshot failed

1. `list_logon_sessions` shows every session, which one is the console, and which one interactive tools would use (`isCurrentInput`).
2. A disconnected session can still have a user and still not be the console. `includeDisconnected` is true by default. Listener rows such as `RDP-Tcp` stay hidden unless `includeListeners` is true.
3. `get_logon_session` adds token elevation and local Administrators membership when Windows allows the token to be opened. A missing token leaves those fields null.
4. Client name and address stay null unless `includeClientAddresses` is true. They are not written to the Vestigo log.
5. The error includes `reason`: `nobody_logged_on`, `only_disconnected_sessions`, `session_0_only`, or `console_session_has_no_token`. `probe_capture_support` and user-scope environment keep `user_not_available` and include the same `reason`.

## Drive the UI

1. Enable Desktop and Input, then set `Desktop:AllowScreenshots` and `Input:AllowInput` to true. Both are off by default.
2. `inspect_screen` returns an annotated image, a numbered element list and a `captureId`.
3. `send_input` with that `captureId` and an `elementId` (or `x`, `y` from the image) clicks that point. A `type` action ignores `elementId` and sends text to the focused field, so click the field first. Set `observe: true` to add the next view to the same call when that capture succeeds. When the actions ran and the capture failed, the result has `performed` and `observeError` and no new view. Read those fields before calling again, because another call repeats the actions.
4. `find_on_screen` locates text or an image template when the element list is not enough.

Input cannot reach a UAC prompt on the secure desktop, the lock screen, or Ctrl+Alt+Del. A prompt on the interactive desktop, used when secure-desktop prompting is off, is reachable. Elevated windows need `elevated: true`.

## Query event logs

1. `list_event_logs` to see which channels are enabled.
2. `query_event_log` on `Application`, `System`, or an `.evtx` file under a read root. Filter by provider, level, event id, or time range.

The Security channel follows Windows ACLs for the service account.

## Switch an audio endpoint

1. Enable Audio. `list_audio_endpoints` shows playback and recording devices, volume, and which process is using them.
2. `set_default_audio_endpoint` stays off until `Audio:AllowSetDefault` is true. Use it when a case needs a specific headset or HDMI output as the default device.

The service context does not see user-session audio. Per-app overrides in Settings are not changed.

## Read a crash and take a dump

1. Enable Diagnostics, Processes, and Jobs. Processes is opt-in and needs Jobs; Diagnostics needs Jobs too. `list_crash_reports` returns Windows Error Reporting metadata for the machine, a user, or both. Dump bytes and the raw report file are not included.
2. `list_processes` and keep the start time of the process you will dump.
3. `capture_process_dump` stays off until `Diagnostics:AllowProcessDumps` is true. Pass that `expectedStartTime` and a new `.dmp` path in a write root. The dump is not uploaded and no download ticket is created. Protected processes and processes on the fixed refusal list are refused.

## Check a lab endpoint

1. Enable Diagnostics and set `Diagnostics:AllowNetworkProbes` to true.
2. Add the host, or the host and port, to `Diagnostics:AllowedProbeDestinations`. The installer does not write that list.
3. `test_endpoint` with `dns`, `tcp`, or `tls`. The probe uses Vestigo's own network context and does not capture packets.

## Read a registry value

1. Enable Registry. It is off by default.
2. `read_registry_value` with a path such as `HKLM\SYSTEM\CurrentControlSet\Services` and `view` `registry64`. `HKCU` means the console user.
3. A path outside the allow list is `path_not_allowed`. Winlogon, Lsa, SAM, and SECURITY are `path_denied` even if you allow a parent. Add `HKLM\SOFTWARE\<Vendor>\<Product>` to `Registry:AllowedPrefixes` when the built-in locations are not enough. That replaces the built-in list.

## Investigate Windows Update and servicing

1. Enable WindowsServicing and Jobs in the installer feature tree or configuration:

   ```json
   {
     "Vestigo": {
       "Modules": {
         "WindowsServicing": { "Enabled": true },
         "Jobs": { "Enabled": true }
       }
     }
   }
   ```

   Restart the Vestigo service after configuration changes, and use a token for
   remote access. This module is separate from Vestigo's Updates self-updater.
2. Call `get_reboot_pending` and `list_pending_file_operations`. Keep the source
   states and malformed-pair warnings: unknown is not the same as no reboot pending.
3. Use `get_windows_update_status` and `get_update_history` for WUA status and
   recent outcomes. `get_update_failure_details` accepts a literal KB, update ID,
   HRESULT or text filter and an earliest time. It collects a bounded history
   window, warning/error events and CBS/DISM log tails. Matching text does not
   prove causation; log timestamps are not parsed by the time filter.
4. `list_windows_updates` starts an offline inventory job. `list_servicing_packages`
   starts a DISM package inventory job; it normally needs elevation. Use
   `wait_job` and paged `read_job_output` to read their JSON results. WUA history
   may have been reset and is not a complete installed-package inventory. Run
   the inventory jobs one at a time: wait for the first job to finish before
   starting the next. If `wait_job` returns Running, continue waiting; its wait
   timeout does not stop the job. A native diagnostic timeout can leave Windows
   work running even after the job fails. Wait before retrying `servicing_busy`.
5. `get_servicing_status` shows CBS and pending-file metadata without an integrity
   check. `get_update_storage_status` shows volume space and fixed-file metadata,
   without recursive cache totals or a WinSxS allocation estimate.

WindowsServicing adds no roots of its own. Check the existing roots before
collecting logs: a fresh MSI install defaults to FixedVolumes with Read, Write
and Execute access, so the paths may already be covered. Both
`get_servicing_logs` and the log tails in `get_update_failure_details` need Read
access to the fixed logs under `%windir%\Logs`. Add a Read root only if those
paths are not already covered. `get_file_lockers` likewise needs the selected
file in a Read root and returns best-effort Restart Manager results without
stopping processes. Raw registry queue paths can be returned outside Read roots,
but queued files are not opened. Read [Security](security.md) first.

The module does not install updates, repair Windows, clear queues or reboot.
Offline applicability work can still update WUA's cache or logs. Timeout or job
cancellation stops waiting rather than aborting native Windows work, and its
concurrency slot stays occupied until that work finishes.

## Collect logs and traces

1. `tail_text_file` or `read_text_file_since` for a growing log under a read root.
2. `search_text` when you need a regex across a directory tree without pulling every file. Use it for line-oriented NDJSON too.
3. `get_json_summary` or `read_json_path` for one UTF-8 JSON snapshot. A query such as `$.status` or `$.items[*]` returns typed values; it does not filter or walk every descendant.
4. `start_capture` / `read_capture` or `convert_etl` for WPP. `get_download_url` or the ticket on a conversion result pulls the file back without pasting the API token into the client.

Keep [the intro](index.md) connection steps nearby: the MSI writes the token into ProgramData, and `/` in a browser repeats the same instructions.
