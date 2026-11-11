---
title: Security
description: Short security model for a Vestigo lab install.
---

# Security model

Vestigo is meant to give an agent administrator-equivalent reach on a lab PC. The defaults try to keep that reach local, token-gated, and off until you ask for it. This page is the booklet summary, not the full policy.

## Tokens

- A fresh MSI install binds every interface and writes a generated bearer token into `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`.
- `dotnet run` defaults to loopback with no tokens.
- When `ApiTokens` is non-empty, `/mcp` requires `Authorization: Bearer <token>`, while `/files` accepts that bearer token or a valid one-time ticket. Tokens are compared by SHA-256 digest and are never written to logs.
- `/` and `/health` stay anonymous.
- Binding beyond loopback without tokens is allowed and logs a startup `SECURITY WARNING`. Use that only on a network you trust.

## Roots

- Trace ETL, PDB, TMF, and conversion-output paths stay inside `AllowedInputRoots` or `AllowedOutputRoots` (default: the protected `traces` folder).
- File, log, process, and device paths use named `Vestigo:Roots` (and optional `VolumeRoots`) with `Read`, `Write`, and `Execute`.
- A root, or a directory containing a root, cannot be deleted or moved. NTFS stream syntax is rejected.
- Write or Execute on a whole volume is a privilege boundary: a client can replace drivers, services, and scripts.

## Modules that stay off

Power is on by default and read-only. It discloses AC status, battery charge, the active scheme name, sleep-policy settings, and the newest Power-Troubleshooter wake text, which can name a timer owner. It does not list power-request owners or change the scheme. System is on by default. `get_boot_state` discloses uptime, an approximate boot time, whether Fast Startup is configured, safe mode, and how many file renames are pending. It does not disclose the queued paths. `list_logon_sessions` and `get_logon_session` disclose who is logged on and which session is the console. Client names and addresses are omitted unless `includeClientAddresses` is true, and they are not written to the log. There is no logoff or session-connect tool.

These stay disabled until you enable the module: **Processes**, **Devices**, **RemoteDebugger**, **Desktop**, **Input**, **Audio**, **DebugOutput**, **Diagnostics**, **Registry**, and **WindowsServicing**. Processes, RemoteDebugger, Diagnostics, and WindowsServicing also need Jobs. Registry does not. With Registry on, clients can read values under the allowed prefixes, and Winlogon stays denied. A symbolic link is followed only when its target also passes those checks. Enabling the powerful ones without tokens is allowed and logs a startup warning. With Diagnostics on, that warning also names process connections, Media Foundation encoders and camera formats, audio format checks, Windows Error Reporting metadata, and Driver Verifier state. Online Authenticode revocation (`Diagnostics:AllowOnlineRevocation`), capture probes (`Diagnostics:AllowCaptureProbes`), network probes (`Diagnostics:AllowNetworkProbes`), audible audio probes (`Diagnostics:AllowAudioPlayback`), process dumps (`Diagnostics:AllowProcessDumps`), and Driver Verifier changes (`Diagnostics:AllowDriverVerifier`) stay off even after Diagnostics is enabled. A UDP probe sends one datagram and can return an unauthenticated STUN reflexive address. Silence is not reported as a firewall block. Network probes also require `Diagnostics:AllowedProbeDestinations`. SetupAPI install history reads only the fixed system logs and does not rank drivers. Capture probes report frame arrival only and do not return pixels. The direct probe connection contacts only the destination listed in `Diagnostics:AllowedProbeDestinations` and does not capture packets. TLS chain building may also contact Authority Information Access issuer servers, and online revocation may contact revocation servers when `Diagnostics:AllowOnlineRevocation` is true. `AllowedProbeDestinations` does not limit those extra contacts. Crash reports are metadata only. Process dumps are not uploaded.

## Separate gates

Even after a module is on, these actions stay off until their own setting is true:

- `Software:AllowUninstall`
- `ScheduledTasks:AllowChanges`
- `Desktop:AllowScreenshots`
- `Desktop:AllowWindowPlacement`
- `Desktop:AllowDisplayLayoutChanges`
- `Desktop:AllowDesktopSessions`
- `Input:AllowInput`
- `Audio:AllowSetDefault`
- `System:AllowBootConfigurationChanges`
- `System:AllowUacChanges`
- `Updates:AllowSelfUpdate`
- `Diagnostics:AllowOnlineRevocation`
- `Diagnostics:AllowCaptureProbes`
- `Diagnostics:AllowNetworkProbes` (also needs `Diagnostics:AllowedProbeDestinations`)
- `Diagnostics:AllowAudioPlayback`
- `Diagnostics:AllowProcessDumps`
- `Diagnostics:AllowDriverVerifier`

Attaching to someone else's ETW session stays off until you list the session name in `AttachableSessions`. `observe_presentation` starts and stops only its own short `Vestigo-` session.

## Screen and input

Screenshots, `inspect_screen`, and `find_on_screen` can disclose whatever is on the console user's screen. UI Automation names and OCR text come back as plain text, which is easier to copy out than pixels. Edit-control values are not read through UI Automation. Text that is visible can still be read by OCR.

`send_input` acts as the console user. Anything on screen can be clicked or typed into. With `elevated: true`, that input is administrator-equivalent while an elevated window is open. The helper sends input only while the input desktop is the session's default desktop. That blocks the lock screen, Ctrl+Alt+Del, and a UAC prompt drawn on the secure desktop. When secure-desktop prompting is off, the UAC prompt stays on the interactive desktop and this check allows the input. The action batch is base64 JSON on the session helper command line. With command-line auditing enabled, event 4688 can record that argument, including typed text. Do not type secrets through `send_input` on a machine that audits process creation.

The MSI preserves a hand-set `Input:AllowInput` on an unelevated upgrade when the Allow input injection feature stays unselected.

## Windows servicing data

WindowsServicing is opt-in and requires Jobs. Enabling it without API tokens
logs a warning about the data it exposes. Update history, package identities,
service/process names, Windows-volume space and fixed-file metadata can identify
software or users on the machine. Raw pending-file queue paths are returned even
outside configured roots; queue inspection never opens those files.

CBS/DISM log content and caller-selected file-locker queries require Read roots.
WindowsServicing adds no roots of its own; existing roots apply, including the fresh MSI default FixedVolumes roots. Logs can contain sensitive machine/user data.
Failure evidence is bounded, and an unavailable source remains unknown or an
explicit error. Empty results are not proof that servicing is healthy.

WUA inventory is offline-only, but can perform substantial local work and write
its own cache/log data. DISM enumeration can write diagnostic logs. Neither tool
installs updates or repairs Windows. No queue editing, cache reset, process
shutdown or system reboot is exposed. Native calls share one slot; timeout or
cancellation stops waiting and retains that slot until Windows returns.

## Plain HTTP

Traffic is not TLS. Tokens, tickets, traces, and screenshots cross the network in the clear. Use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine. One-time `/files` tickets expire, bind to one path, and are consumed on first use; treat the URL as a secret until then. A `take_screenshot` share URL is the same kind of secret for a downscaled preview: it lasts 10 minutes, works once, and does not require the API token.

Back to the [intro](index.md), [features](features.md), [developers](developers.md), or [QA](qa.md).
