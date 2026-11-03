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
3. `take_screenshot` of the desktop, one display, one window, or a region. Pass `saveTo` in a write root; the result includes a one-time download link when tokens are configured.

The lock screen and UAC secure desktop fail with `desktop_unavailable`. A console user must be logged on.

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

## Collect logs and traces

1. `tail_text_file` or `read_text_file_since` for a growing log under a read root.
2. `search_text` when you need a regex across a directory tree without pulling every file.
3. `start_capture` / `read_capture` or `convert_etl` for WPP. `get_download_url` or the ticket on a conversion result pulls the file back without pasting the API token into the client.

Keep [the intro](index.md) connection steps nearby: the MSI writes the token into ProgramData, and `/` in a browser repeats the same instructions.
