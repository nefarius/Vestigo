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

These stay disabled until you enable the module: **Processes**, **Devices**, **RemoteDebugger**, **Desktop**, **Audio**, **DebugOutput**, **Diagnostics**, and **WindowsServicing**. Processes, RemoteDebugger, Diagnostics, and WindowsServicing also need Jobs. Enabling the powerful ones without tokens is allowed and logs a startup warning. With Diagnostics on, that warning also names process connections, Media Foundation encoders and camera formats, audio format checks, and Windows Error Reporting metadata. Online Authenticode revocation (`Diagnostics:AllowOnlineRevocation`), capture probes (`Diagnostics:AllowCaptureProbes`), network probes (`Diagnostics:AllowNetworkProbes`), audible audio probes (`Diagnostics:AllowAudioPlayback`), and process dumps (`Diagnostics:AllowProcessDumps`) stay off even after Diagnostics is enabled. Network probes also require `Diagnostics:AllowedProbeDestinations`. SetupAPI install history reads only the fixed system logs and does not rank drivers. Capture probes report frame arrival only and do not return pixels. The direct probe connection contacts only the destination listed in `Diagnostics:AllowedProbeDestinations` and does not capture packets. TLS chain building may also contact Authority Information Access issuer servers, and online revocation may contact revocation servers when `Diagnostics:AllowOnlineRevocation` is true. `AllowedProbeDestinations` does not limit those extra contacts. Crash reports are metadata only. Process dumps are not uploaded.

## Separate gates

Even after a module is on, these actions stay off until their own setting is true:

- `Software:AllowUninstall`
- `ScheduledTasks:AllowChanges`
- `Desktop:AllowScreenshots`
- `Audio:AllowSetDefault`
- `System:AllowBootConfigurationChanges`
- `System:AllowUacChanges`
- `Updates:AllowSelfUpdate`
- `Diagnostics:AllowOnlineRevocation`
- `Diagnostics:AllowCaptureProbes`
- `Diagnostics:AllowNetworkProbes` (also needs `Diagnostics:AllowedProbeDestinations`)
- `Diagnostics:AllowAudioPlayback`
- `Diagnostics:AllowProcessDumps`

Attaching to someone else's ETW session stays off until you list the session name in `AttachableSessions`.

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

Traffic is not TLS. Tokens, tickets, traces, and screenshots cross the network in the clear. Use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine. One-time `/files` tickets expire, bind to one path, and are consumed on first use; treat the URL as a secret until then.

Back to the [intro](index.md), [features](features.md), [developers](developers.md), or [QA](qa.md).
