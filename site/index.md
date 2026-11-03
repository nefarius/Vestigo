---
title: Intro
description: Vestigo is a Windows MCP server for driver and application development.
---

# A Windows lab that an agent can operate

Vestigo is a Windows MCP server for driver and application development. It runs as a Windows service and hosts a stateless Streamable HTTP endpoint at `/mcp`. A remote agent uses that endpoint to convert WPP traces, capture ETW live, move files, inspect the machine, and change the things a driver or QA lab actually needs changed.

This booklet is for developers and QA personnel who will point an MCP client at a lab PC. [Features](features.md) is the catalogue. [Developers](developers.md) and [QA](qa.md) are the two common workflows. [Security](security.md) is the short model you should read before binding anything beyond loopback.

## Who it is for

- **Driver and application developers** who want an agent to install a build, start a WPP session, resolve PDBs, read `OutputDebugString`, and attach Visual Studio's remote debugger.
- **QA and test-rig operators** who want the same agent to install or uninstall a build, toggle devices, take a screenshot, query event logs, switch an audio endpoint, and pull logs and traces back as artifacts.

It is Windows only. There is no management UI: opening `/` in a browser shows connection instructions, and `/health` is an anonymous probe.

## How an agent talks to it

The client speaks MCP over Streamable HTTP at `http://127.0.0.1:5098/mcp`. When `ApiTokens` is set, every `/mcp` call needs `Authorization: Bearer <token>`. File-transfer tools return short-lived, single-use `/files` URLs, so a client does not need the API token to fetch a screenshot or a converted trace.

A fresh MSI install binds every interface and writes a generated bearer token. `dotnet run` from the repository uses loopback and no tokens until you add some.

## Quick start

1. Install the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0. The MSI refuses to continue without them. Either install the ASP.NET Core 10.0 Hosting Bundle from https://dotnet.microsoft.com/download/dotnet/10.0, which includes both, or run these from an elevated prompt:

   ```powershell
   winget install --id Microsoft.DotNet.Runtime.10 --architecture x64
   winget install --id Microsoft.DotNet.AspNetCore.10 --architecture x64
   ```

   `--architecture x64` matters on Windows 11 ARM64, because a native ARM64 runtime does not satisfy the MSI. `dotnet run` does not need this step, because the .NET 10 SDK already includes both runtimes.
2. Install the MSI, or run `dotnet run --project server` with the .NET 10 SDK.
3. Open `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. After a default MSI install this file contains a generated `ApiTokens` entry; copy that token. `dotnet run` stays anonymous until you add tokens.
4. Point an MCP client at `http://127.0.0.1:5098/mcp`. When `ApiTokens` is non-empty, send `Authorization: Bearer <token>`. Opening `http://127.0.0.1:5098/` in a browser shows the same connection instructions.

The default MSI listen mode is any address with a token (`0.0.0.0:5098`). Loopback still works on the same machine. Binding beyond loopback without tokens logs a startup warning. Traffic is plain HTTP; use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine.

Installed MSI needs Windows 10 or later (x64; Windows 11 ARM64 through x64 emulation), the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 (compatible 10.0.x patches; the highest Microsoft.NETCore.App patch must be at least the highest Microsoft.AspNetCore.App patch; the ASP.NET Core 10.0 Hosting Bundle also satisfies this; on ARM64 these are the x64 runtimes under `%ProgramFiles%\dotnet\x64`), and an elevated service account for realtime ETW sessions and most mutating tools. When either runtime is missing, the installer stops and names that requirement.
