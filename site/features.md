---
title: Features
description: Vestigo feature booklet for developers and QA personnel.
---

# Feature booklet

Each card is one area of the MCP surface. **On by default** is available after a stock install. **Opt-in** stays off until that module is enabled. **Gated** needs a second setting even after the module is on.

See [Developers](developers.md) and [QA](qa.md) for the workflows that combine these tools. Read [Security](security.md) before enabling opt-in modules on a shared machine.

<div class="cards">
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>WPP and ETW tracing</h2>
    <p>Inspect a WPP PDB, validate PDB or TMF sources, scan ETL metadata, and convert an ETL file to JSON or NDJSON. <code>start_capture</code> creates a <code>Vestigo-</code> realtime session from provider GUIDs or PDB paths. Follow it with long-polling <code>read_capture</code>. <code>attach_etw_session</code> can read an existing session once you list it in <code>AttachableSessions</code>.</p>
    <p>Vestigo decodes events; it does not write a raw ETL file. The NT Kernel Logger is not supported.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Files and text search</h2>
    <p>Named roots with <code>Read</code>, <code>Write</code>, and <code>Execute</code> confine every path. List, copy, move, delete, and mkdir stay inside those roots. <code>fetch_file</code> pulls HTTP(S) or UNC into a root as a job. Upload and download URLs can carry a one-time ticket so the API token never goes into a curl command.</p>
    <p><code>tail_text_file</code> and <code>read_text_file_since</code> follow a growing log. <code>search_text</code> runs bundled ripgrep across a root and returns only the matching lines. <code>read_json_path</code> and <code>get_json_summary</code> read one UTF-8 JSON document without downloading it.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Registry opt-in</span></p>
    <h2>Registry</h2>
    <p><code>read_registry_value</code>, <code>list_registry_values</code>, and <code>get_registry_metadata</code> read one allow-listed key. An empty allow list uses built-in Windows locations. A denied prefix, including Winlogon, wins. <code>HKCU</code> is the console user. Choose <code>registry64</code> or <code>registry32</code> explicitly.</p>
    <p>There is no write tool. A binary larger than <code>Registry:MaxBinaryBytes</code> is not returned. Values are not written to the log.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill optin">Processes opt-in</span></p>
    <h2>Processes and services</h2>
    <p>List, start, stop, restart, and reconfigure Win32 services and kernel drivers. Create or delete a <code>KERNEL_DRIVER</code> service through bundled nefcon. Protected service names cannot be mutated.</p>
    <p>The Processes module is off until enabled. Then <code>run_process</code> starts a job as the service account or the console user, <code>close_app</code> posts <code>WM_CLOSE</code>, and <code>kill_process</code> terminates by pid or name.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill gated">Changes gated</span></p>
    <h2>Scheduled tasks</h2>
    <p>List and inspect local Task Scheduler tasks, including principal, actions, triggers, and definition XML. History comes from the Task Scheduler Operational channel when that log is enabled.</p>
    <p>Run, stop, enable, disable, and delete stay off until <code>ScheduledTasks:AllowChanges</code> is true. Vestigo does not create or edit task definitions. Paths under <code>\Microsoft\Windows\</code> cannot be deleted or disabled.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill gated">Uninstall gated</span></p>
    <h2>Software</h2>
    <p>Inventory installed programs from the Uninstall registry keys, including publisher, version, installer type, and whether a silent uninstall exists.</p>
    <p><code>uninstall_software</code> stays off until <code>Software:AllowUninstall</code> is true. It covers MSI, Burn, Inno Setup, NSIS, and entries that publish a quiet uninstall string. Per-user entries and AppX/MSIX packages are refused.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span></p>
    <h2>Devices and drivers</h2>
    <p>Install and uninstall driver packages, create and remove ROOT device nodes, edit class filters, run <code>[DefaultInstall]</code> INFs, list the driver store, and enable, disable, or restart a device. Queries run in-process; mutating calls go through bundled nefcon.</p>
    <p>Installing a driver is kernel-mode code execution. Enable this module only on machines you intend to debug drivers on.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span> <span class="pill gated">Screenshots gated</span></p>
    <h2>Desktop and screenshots</h2>
    <p>List and inspect windows, capture window creation, show, hide, foreground and location changes, and list displays on the console user's desktop. Screenshots stay off until <code>Desktop:AllowScreenshots</code> is true. Moving a window by its client rectangle stays off until <code>Desktop:AllowWindowPlacement</code> is true. Temporary display-layout changes stay off until <code>Desktop:AllowDisplayLayoutChanges</code> is true. Named test desktops stay off until <code>Desktop:AllowDesktopSessions</code> is true. Vestigo assigns the desktop name, refuses the lock screen and UAC, and restores the recorded input desktop on close. Vestigo does not create virtual displays. Capture the virtual screen, one display, one window, or a rectangle, and optionally save a PNG into a write root. <code>share: true</code> adds a 10-minute, single-use link to the downscaled preview so a person can open or copy it when the chat does not show the MCP image.</p>
    <p>The lock screen and UAC secure desktop return <code>desktop_unavailable</code>. Some GPU or DRM windows capture black.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Desktop opt-in</span> <span class="pill gated">Screenshots gated</span> <span class="pill gated">Placement gated</span> <span class="pill gated">Display layout gated</span></p>
    <h2>Screen inspection (UI Automation, OCR, OpenCV)</h2>
    <p><code>inspect_screen</code> describes the console user's screen for an agent that cannot see the image. It returns an annotated capture, a text element list, and a <code>captureId</code>. Numbered boxes are green for UI Automation, blue for Windows OCR, and red for OpenCV regions. UI Automation supplies names and roles and never reads edit-control values. OCR uses the console user's installed languages. OpenCV adds contour regions only where the other two found nothing.</p>
    <p><code>diffAgainst</code> compares an earlier capture of the same target and size and reports the share of changed pixels and the regions that changed. <code>find_on_screen</code> locates text in element names and OCR, or matches a PNG or JPEG template from a Read root, or a crop of an earlier capture, at more than one scale.</p>
    <p>Both tools share the screenshot gate. OCR needs an installed OCR language for the console user. Owner-drawn and game surfaces fall back to unnamed OpenCV regions. A <code>captureId</code> lasts about two minutes, and the server keeps at most eight.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span> <span class="pill gated">Input gated</span></p>
    <h2>Mouse and keyboard input</h2>
    <p>Inject mouse movement, clicks, scrolling, drags, typing and key chords as the console user. Pass the <code>captureId</code> from <code>inspect_screen</code> with an <code>elementId</code>, or <code>x</code> and <code>y</code> in that image. <code>observe: true</code> waits, then returns a fresh <code>inspect_screen</code> result in the same call. The Input module needs Desktop, and input stays off until <code>Input:AllowInput</code> is true.</p>
    <p>Input cannot reach the secure desktop (UAC prompts, the lock screen, Ctrl+Alt+Del). Elevated windows need <code>elevated: true</code>. That is administrator-equivalent while an elevated window is open, and Windows can still drop the input silently.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span> <span class="pill gated">Default switch gated</span></p>
    <h2>Audio</h2>
    <p>List Core Audio playback and recording endpoints, volume, mute, a short peak sample, and the sessions using each device. Switching the default device stays off until <code>Audio:AllowSetDefault</code> is true.</p>
    <p>The service context does not see user audio sessions. Per-app overrides are not shown or changed.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Environment variables</h2>
    <p>Read and set machine, console-user, and process variables. Machine and user values come from the registry and are returned unexpanded. User scope needs someone logged on at the console.</p>
    <p>Names in <code>Environment:ProtectedNames</code> cannot be set or unset. Values are not written to the Vestigo log.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill gated">BCD writes gated</span> <span class="pill gated">UAC writes gated</span></p>
    <h2>System and boot security</h2>
    <p><code>get_system_info</code> reports Windows edition, display version, bitness, and UAC state. <code>get_boot_state</code> reports uptime, an approximate boot time, whether Fast Startup is configured, safe mode, and a pending file-rename count. That configuration does not prove the previous boot was hybrid, and the count does not include paths. <code>get_boot_security</code> reports UEFI, Secure Boot, test-signing, and WHQL developer test mode. <code>get_uac</code> reports the slider level and EnableLUA. <code>list_logon_sessions</code> shows who is logged on and which session interactive tools would use. Client names and addresses stay off unless requested, and they are not written to the log. When an interactive tool finds no console user, the error includes <code>reason</code>: <code>nobody_logged_on</code>, <code>only_disconnected_sessions</code>, <code>session_0_only</code>, or <code>console_session_has_no_token</code>.</p>
    <p><code>set_test_signing</code> and <code>set_whql_developer_test_mode</code> stay off until <code>System:AllowBootConfigurationChanges</code> is true. <code>set_uac</code> stays off until <code>System:AllowUacChanges</code> is true. Test-signing is refused while Secure Boot is on. Vestigo never reboots the machine.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Power</h2>
    <p><code>get_power_state</code> reports AC status, battery presence, percent, and charging separately, the active scheme, approximate last sleep and wake times, and platform capabilities. <code>get_sleep_configuration</code> reports AC and DC lid, timeout, wake-timer, and hybrid-sleep settings.</p>
    <p>A missing policy value is null, not <code>doNothing</code>. A machine with no battery leaves the battery fields null. There is no scheme write and no list of power-request owners.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span></p>
    <h2>Debug output</h2>
    <p>Capture Win32 <code>OutputDebugString</code> through bundled DebugViewConsole. Start a filtered capture, then follow it with long-polling <code>read_debug_output</code>.</p>
    <p>Windows allows only one <code>DBWIN_BUFFER</code> reader. A debugger-attached process sends output to the debugger instead. Kernel <code>DbgPrint</code> is not captured.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span> <span class="pill gated">Online revocation gated</span></p>
    <h2>Diagnostics</h2>
    <p><code>inspect_binary</code> reads architecture, characteristics, version, and the CodeView PDB file name. <code>verify_file_signature</code> checks an embedded or catalog Authenticode signature. <code>list_process_modules</code> lists modules loaded in a process and returns root aliases only. <code>get_device_install_history</code> reads the fixed SetupAPI dev logs. <code>explain_device_problem</code> states the documented CM_PROB meaning and cites observed evidence.</p>
    <p>A valid signature is not permission to load a kernel driver. Online revocation stays off until <code>Diagnostics:AllowOnlineRevocation</code> is true. Absolute module paths are never returned. Install history does not rank drivers. Graphics, network, media, and crash tools below are part of this same module.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Diagnostics opt-in</span> <span class="pill gated">Capture probes gated</span></p>
    <h2>Graphics</h2>
    <p><code>list_graphics_adapters</code> lists DXGI adapters and the outputs attached to them, including software and remote adapters. <code>probe_capture_support</code> observes whether frames arrive for the primary display or a GDI device name.</p>
    <p>Capture probes stay off until <code>Diagnostics:AllowCaptureProbes</code> is true and never return pixels. Windows Graphics Capture is unsupported. A console user must be logged on.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Diagnostics opt-in</span> <span class="pill gated">Network probes gated</span></p>
    <h2>Network checks</h2>
    <p><code>list_process_connections</code> lists the TCP endpoints and UDP bindings owned by one process. Remote addresses stay omitted unless you ask for them. <code>test_endpoint</code> resolves a host, opens TCP, or completes a TLS handshake from Vestigo's own network context.</p>
    <p>The probe stays off until <code>Diagnostics:AllowNetworkProbes</code> is true and the host, or host and port, is listed in <code>Diagnostics:AllowedProbeDestinations</code>. It does not capture packets. Online TLS revocation uses the same revocation gate as signature checks.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Diagnostics opt-in</span> <span class="pill gated">Playback gated</span></p>
    <h2>Media checks</h2>
    <p><code>list_video_encoders</code> enumerates Media Foundation video encoders and does not activate them. <code>list_camera_formats</code> lists cameras and, for one device, the formats it advertises. <code>probe_audio_format</code> checks a shared-mode PCM format on an endpoint.</p>
    <p>A format check does not emit sound. A synthetic tone plays only when <code>Diagnostics:AllowAudioPlayback</code> is true. Camera listing does not read samples. Encoder enumeration is not proof that a frame can be encoded.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Diagnostics opt-in</span> <span class="pill gated">Dumps gated</span></p>
    <h2>Crash reports and dumps</h2>
    <p><code>list_crash_reports</code> reads Windows Error Reporting metadata from the machine and user stores: application, event type, and signature values. A dump beside a report is a file name and length only.</p>
    <p><code>capture_process_dump</code> stays off until <code>Diagnostics:AllowProcessDumps</code> is true. It writes a minidump into a write root, refuses protected processes and processes on the fixed refusal list, and does not upload the file or create a download ticket. A full dump can contain process memory.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Event logs</h2>
    <p>List channels and query a live log or an <code>.evtx</code> file under a read root. Filter by provider, level, event id, time range, or a raw XPath. Results are newest first and capped.</p>
    <p>The Security channel follows Windows ACLs for the service account. Rendered messages are null when the publisher is not installed.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span> <span class="pill">Read-only</span> <span class="pill">Needs Jobs</span></p>
    <h2>Windows Update and servicing</h2>
    <p><code>get_reboot_pending</code> combines registry and WUA signals as pending, not pending, or unknown. <code>list_pending_file_operations</code> reads both Session Manager rename/delete queues, preserving raw paths and malformed-pair warnings without opening or changing queued files.</p>
    <p><code>list_windows_updates</code> starts an offline WUA inventory job. <code>get_update_history</code> returns bounded history with HRESULTs, and <code>get_windows_update_status</code> snapshots WUA and relevant service states. A stopped trigger-start service is not automatically a blocker.</p>
    <p><code>get_servicing_status</code> reports CBS and pending-file metadata without an integrity scan. <code>list_servicing_packages</code> starts a DISM package inventory job, normally requiring elevation.</p>
    <p><code>get_update_failure_details</code> collects bounded history, events and log evidence. <code>get_servicing_logs</code> tails fixed CBS/DISM logs through Read roots. <code>get_update_storage_status</code> reports Windows-volume space and fixed-file metadata. <code>get_file_lockers</code> uses Restart Manager for one file in a Read root without stopping processes.</p>
    <p>This is separate from Vestigo self-updates. No Windows update install, repair, online scan, cache reset, queue edit or reboot is exposed. Evidence is bounded and does not establish causation. Native timeout or cancellation stops waiting; the concurrency slot remains held until Windows returns.</p>
    <p><a href="https://github.com/nefarius/Vestigo/blob/master/docs/windows-servicing.md">Full tool contracts and limitations</a></p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Jobs</h2>
    <p>Long work (process launch, silent uninstall, file fetch, remote-debugger install, process dumps, Windows update inventory and servicing-package inventory) returns a job id. <code>wait_job</code>, <code>read_job_output</code>, and <code>cancel_job</code> wait for, page the output of, or cancel a job. Process jobs can stop their process tree; native servicing calls are not forcibly terminated.</p>
    <p>Finished jobs are kept for a bounded retention period, then discarded.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill gated">Install gated</span></p>
    <h2>Vestigo self-updates</h2>
    <p><code>check_for_updates</code> fetches the signed Buildbot feed, verifies the minisign sidecar, and compares it to the installed version. The check is available whenever the Updates module is on.</p>
    <p><code>start_self_update</code> stays off until <code>Updates:AllowSelfUpdate</code> is true and the Automatic updates feature is installed. It launches the packaged Vicius updater silently; the MSI restarts the service.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill optin">Opt-in</span></p>
    <h2>Remote debugger</h2>
    <p>Install Visual Studio Remote Debugging Tools for 2022 or 2026, prepare the firewall, start <code>msvsmon.exe</code>, and stop it. Status reports every discovered monitor and whether Vestigo started it.</p>
    <p>The module needs Processes and Jobs. A running monitor lets a remote debugger execute code on the machine; treat the token as administrator-equivalent.</p>
  </article>
</div>
