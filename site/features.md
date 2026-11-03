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
    <p><code>tail_text_file</code> and <code>read_text_file_since</code> follow a growing log. <code>search_text</code> runs bundled ripgrep across a root and returns only the matching lines.</p>
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
    <p>List windows and displays on the console user's desktop, including titles, bounds, and monitor topology. Screenshots stay off until <code>Desktop:AllowScreenshots</code> is true. Capture the virtual screen, one display, one window, or a rectangle, and optionally save a PNG into a write root.</p>
    <p>The lock screen and UAC secure desktop return <code>desktop_unavailable</code>. Some GPU or DRM windows capture black.</p>
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
    <p><code>get_system_info</code> reports Windows edition, display version, bitness, and UAC state. <code>get_boot_security</code> reports UEFI, Secure Boot, test-signing, and WHQL developer test mode. <code>get_uac</code> reports the slider level and EnableLUA.</p>
    <p><code>set_test_signing</code> and <code>set_whql_developer_test_mode</code> stay off until <code>System:AllowBootConfigurationChanges</code> is true. <code>set_uac</code> stays off until <code>System:AllowUacChanges</code> is true. Test-signing is refused while Secure Boot is on. Vestigo never reboots the machine.</p>
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
    <p><code>capture_process_dump</code> stays off until <code>Diagnostics:AllowProcessDumps</code> is true. It writes a minidump into a write root, refuses protected and critical processes, and does not upload the file or create a download ticket. A full dump can contain process memory.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Event logs</h2>
    <p>List channels and query a live log or an <code>.evtx</code> file under a read root. Filter by provider, level, event id, time range, or a raw XPath. Results are newest first and capped.</p>
    <p>The Security channel follows Windows ACLs for the service account. Rendered messages are null when the publisher is not installed.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span></p>
    <h2>Jobs</h2>
    <p>Long work (process launch, silent uninstall, file fetch, remote-debugger install, process dumps) returns a job id. <code>wait_job</code>, <code>read_job_output</code>, and <code>cancel_job</code> page the output and stop the tree if needed.</p>
    <p>Finished jobs are kept for a bounded retention period, then discarded.</p>
  </article>
  <article class="card">
    <p class="pills"><span class="pill on">On by default</span> <span class="pill gated">Install gated</span></p>
    <h2>Updates</h2>
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
