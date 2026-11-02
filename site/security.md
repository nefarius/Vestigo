---
title: Security
description: Short security model for a Vestigo lab install.
---

# Security model

Vestigo is meant to give an agent administrator-equivalent reach on a lab PC. The defaults try to keep that reach local, token-gated, and off until you ask for it. This page is the booklet summary, not the full policy.

## Tokens

- A fresh MSI install binds every interface and writes a generated bearer token into `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`.
- `dotnet run` defaults to loopback with no tokens.
- When `ApiTokens` is non-empty, `/mcp` and `/files` need `Authorization: Bearer <token>`. Tokens are compared by SHA-256 digest and are never written to logs.
- `/` and `/health` stay anonymous.
- Binding beyond loopback without tokens is allowed and logs a startup `SECURITY WARNING`. Use that only on a network you trust.

## Roots

- Trace ETL, PDB, TMF, and conversion-output paths stay inside `AllowedInputRoots` or `AllowedOutputRoots` (default: the protected `traces` folder).
- File, log, process, and device paths use named `Vestigo:Roots` (and optional `VolumeRoots`) with `Read`, `Write`, and `Execute`.
- A root, or a directory containing a root, cannot be deleted or moved. NTFS stream syntax is rejected.
- Write or Execute on a whole volume is a privilege boundary: a client can replace drivers, services, and scripts.

## Modules that stay off

These stay disabled until you enable the module: **Processes**, **Devices**, **RemoteDebugger**, **Desktop**, **Audio**, and **DebugOutput**. Processes and RemoteDebugger also need Jobs. Enabling the powerful ones without tokens is allowed and logs a startup warning.

## Separate gates

Even after a module is on, these writes stay off until their own setting is true:

- `Software:AllowUninstall`
- `ScheduledTasks:AllowChanges`
- `Desktop:AllowScreenshots`
- `Audio:AllowSetDefault`
- `System:AllowBootConfigurationChanges`

Attaching to someone else's ETW session stays off until you list the session name in `AttachableSessions`.

## Plain HTTP

Traffic is not TLS. Tokens, tickets, traces, and screenshots cross the network in the clear. Use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine. One-time `/files` tickets expire, bind to one path, and are consumed on first use; treat the URL as a secret until then.

Back to the [intro](index.md), [features](features.md), [developers](developers.md), or [QA](qa.md).
