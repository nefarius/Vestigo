# <img src="assets/NSS-128x128.png" align="left" />Vestigo

[![.NET](https://github.com/nefarius/Vestigo/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/Vestigo/actions/workflows/build.yml)
![Requirements](https://img.shields.io/badge/Requires-.NET%2010-blue.svg)
![Windows only](https://img.shields.io/badge/Windows-10%2B-red)
[![Assisted by Cursor AI](https://img.shields.io/badge/Assisted%20by-Cursor%20AI-8B5CF6?style=flat)](https://cursor.com/)

Vestigo is a Windows MCP server for driver and application development. It hosts a stateless Streamable HTTP endpoint at `/mcp` and runs as a Windows service. Besides offline WPP conversion and realtime ETW capture, it can transfer files, inspect and control processes and services, inventory and uninstall software, manage devices and drivers, capture Win32 debug output, read environment variables and event logs, and install the Visual Studio remote debugger.

It is remote-capable. A fresh MSI install binds every interface and writes a generated bearer token. `dotnet run` uses the repository `appsettings.json`, which defaults to loopback without tokens. WPP decoding uses [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW) with PDB or TMF files.

## Requirements

Installed MSI:

- Windows 10 or later (x64)
- The x64 .NET 10 ASP.NET Core runtime
- An elevated service account for realtime ETW sessions and most mutating tools

Source / `dotnet run`:

- Windows 10 or later
- The .NET 10 SDK
- MCP client support for Streamable HTTP

Reading an existing ETL file does not by itself require elevation. Creating a realtime session, controlling services, installing drivers, launching processes, and silent uninstall generally do.

## Quick start

1. Install the MSI, or run `dotnet run --project server` with the SDK.
2. Open `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. After a default MSI install this file contains a generated `ApiTokens` entry; copy that token. `dotnet run` stays anonymous until you add tokens.
3. Point an MCP client at `http://127.0.0.1:5098/mcp`. When `ApiTokens` is non-empty, send `Authorization: Bearer <token>`.

The default MSI listen mode is any address with a token (`0.0.0.0:5098`). Loopback still works on the same machine. `/health` is always anonymous. `/mcp` and `/files` are anonymous only while `ApiTokens` is empty. Binding beyond loopback without tokens logs a startup warning.

## Projects

- [`Vestigo.Server`](server) — ASP.NET Core host, MCP tools, capture manager, and feature modules.
- [`Vestigo.Installer.Actions`](installer/Vestigo.Installer.Actions) — net48 MSI custom actions and the pure listen-settings logic shared with the tests.
- [`Vestigo.Installer`](installer) — WixSharp project that builds the per-machine x64 MSI and Windows service.
- [`tests/Vestigo.Server.Tests`](tests/Vestigo.Server.Tests) — path, authentication, capture, module, installer, and documentation tests.

## Security

- Tokens are compared by SHA-256 digest and are never written to logs.
- Trace ETL, PDB, TMF, and conversion-output paths stay inside `AllowedInputRoots` or `AllowedOutputRoots`.
- File, log, process, and device paths use named `Vestigo:Roots` with `Read`, `Write`, and `Execute`.
- Output files are not overwritten unless the tool argument says so.
- Event count, input size, output size, capture duration, job output, and spool retention are bounded.

See [docs/security.md](docs/security.md) and [docs/configuration.md](docs/configuration.md).

## Build and installer

Windows publish output is framework-dependent, ReadyToRun, and `win-x64`:

```powershell
.\build.ps1 PublishLocal
```

On Windows, with WiX 5.0.2, `WixToolset.UI.wixext/5.0.2`, and `WixToolset.Firewall.wixext/5.0.2`:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Firewall.wixext/5.0.2
.\build.ps1 BuildInstaller
```

`BuildInstaller` needs network access: it downloads the pinned signed [Vicius](https://github.com/nefarius/vicius) 1.20.0 x64 updater, the pinned signed [nefcon](https://github.com/nefarius/nefcon) 1.21.0 CLI, and the pinned [DebugView++](https://github.com/CobaltFusion/DebugViewPP) 1.30.0.0 `DebugViewConsole.exe`. It verifies each archive or asset hash (and the Vicius and nefcon Authenticode signatures) before packaging them with the server. nefcon and DebugViewConsole are installed under `tools\`.

The MSI is `publish-x64\installer\Vestigo.msi`. Its listen/security page offers four modes (localhost or any address, each with or without an API token; any address with a token is the default) and writes them to the ProgramData `appsettings.json`. A feature tree then selects the modules to enable, independent of the listen mode (a no-token server with powerful modules is allowed and logs a startup warning); see [docs/windows-service.md](docs/windows-service.md) for the silent-install properties. A local build is unsigned. Tagging `v*` signs `Vestigo.exe`, `Vestigo.dll`, and the MSI through SignRelay, then publishes `updates.json` and `updates.json.minisig` beside the MSI on Buildbot. If `LICENSE` changes, update `installer\License.rtf` as well. Third-party terms for the updater, nefcon, and DebugView++ are in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

## Limitations

- Windows only. The NT Kernel Logger and kernel-mode realtime providers are not supported.
- Vestigo decodes events; it does not record a raw ETL file.
- There is no server push. Follow a live capture by long-polling `read_capture` or `read_debug_output` with `waitSeconds`; external sessions can be read with `attach_etw_session` once allowed by `Vestigo:AttachableSessions`.
- Debug-output capture needs an exclusive `DBWIN_BUFFER` reader. Another DebugView instance fails the start. Debugger-attached processes and kernel `DbgPrint` are not captured.
- There is no UI. Traffic is plain HTTP; use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine.
- WPP enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` remain numeric in the upstream decoder.
- Symbol resolution uses the released `Nefarius.Utilities.ETW` package.
- Silent uninstall covers MSI, Burn, Inno Setup, NSIS and entries that publish a `QuietUninstallString`. Other installers are refused, per-user entries cannot be uninstalled, and AppX/MSIX packages are not listed.
- Vestigo never triggers a reboot; tools report or schedule reboot-time work.

## Documentation

- [Architecture](docs/architecture.md)
- [MCP tools](docs/tools.md)
- [MCP tool catalog](docs/generated/mcp-tools.md)
- [Configuration](docs/configuration.md)
- [Configuration defaults](docs/generated/configuration.md)
- [Security](docs/security.md)
- [Windows service](docs/windows-service.md)
- [Symbols](docs/symbols.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Release](docs/release.md)

## Sources & 3rd party credits

- [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW)
- [Nefarius.Utilities.DeviceManagement](https://github.com/nefarius/Nefarius.Utilities.DeviceManagement)
- [Model Context Protocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [Serilog](https://github.com/serilog/serilog)
- [CliWrap](https://github.com/Tyrrrz/CliWrap)
- [MinVer](https://github.com/adamralph/minver)
- [WixSharp](https://github.com/oleg-shilo/wixsharp)
- [WiX Toolset](https://wixtoolset.org/)
- [Vicius](https://github.com/nefarius/vicius)
- [nefcon](https://github.com/nefarius/nefcon)
