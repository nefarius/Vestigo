# <img src="assets/NSS-128x128.png" align="left" />Vestigo

[![.NET](https://github.com/nefarius/Vestigo/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/Vestigo/actions/workflows/build.yml)
![Requirements](https://img.shields.io/badge/Requires-.NET%2010-blue.svg)
![Windows only](https://img.shields.io/badge/Windows-10%2B-red)
[![Assisted by Cursor AI](https://img.shields.io/badge/Assisted%20by-Cursor%20AI-8B5CF6?style=flat)](https://cursor.com/)

Vestigo is a Windows MCP server for driver and application development. It hosts a stateless Streamable HTTP endpoint at `/mcp` and runs as a Windows service. Besides offline WPP conversion and realtime ETW capture, it can transfer files, inspect and control processes and services, list, inspect, and run scheduled tasks, inventory and uninstall software, manage devices and drivers, list windows and displays and take gated screenshots, list audio endpoints and switch the default device, inspect Windows version, boot-security, and UAC state with gated test-signing, WHQL developer-mode, and UAC changes, capture Win32 debug output, read and set environment variables, tail and regex-search text logs on the machine, query event logs, run background jobs, install the Visual Studio remote debugger, and check for or apply a signed self-update.

It is remote-capable. A fresh MSI install binds every interface and writes a generated bearer token. `dotnet run` uses the repository `appsettings.json`, which defaults to loopback without tokens. WPP decoding uses [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW) with PDB or TMF files. A remote agent can resolve those PDBs from an installed binary, the local cache, a configured symbol server, or a file uploaded into the built-in `traces` folder.

## Requirements

Installed MSI:

- Windows 10 or later (x64). Windows 11 on ARM64 is supported through x64 emulation.
- The x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 (any 10.0.x patch; the ASP.NET Core 10.0 Hosting Bundle also satisfies this). On ARM64, install the x64 runtimes (they live under `%ProgramFiles%\dotnet\x64`); a native ARM64 runtime does not satisfy the MSI.
- An elevated service account for realtime ETW sessions and most mutating tools

Source / `dotnet run`:

- Windows 10 or later
- The .NET 10 SDK
- MCP client support for Streamable HTTP

Reading an existing ETL file does not by itself require elevation. Creating a realtime session, controlling services, installing drivers, launching processes, and silent uninstall generally do. `close_app` (default), `take_screenshot`, Desktop and Audio calls with `context: interactive`, and `run_process` with `context: interactive` need the service to run as SYSTEM with a user logged on at the console. Desktop and Audio calls and `close_app` with `context: service` run in the service session and do not need a console user.

## Quick start

1. Install the MSI, or run `dotnet run --project server` with the SDK.
2. Open `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. After a default MSI install this file contains a generated `ApiTokens` entry; copy that token. `dotnet run` stays anonymous until you add tokens.
3. Point an MCP client at `http://127.0.0.1:5098/mcp`. When `ApiTokens` is non-empty, send `Authorization: Bearer <token>`. Opening `http://127.0.0.1:5098/` in a browser shows the same connection instructions.

The default MSI listen mode is any address with a token (`0.0.0.0:5098`). Loopback still works on the same machine. `/` is an anonymous informational page. `/health` is always anonymous. `/mcp` and `/files` are anonymous only while `ApiTokens` is empty. File-transfer tools return short-lived, single-use `/files` URLs, so clients never need the API token to fetch a screenshot or converted trace. Binding beyond loopback without tokens logs a startup warning.

## Projects

- [`Vestigo.Server`](server) — ASP.NET Core host, MCP tools, capture manager, and feature modules.
- [`build`](build) — Fallout build project: `build.ps1` targets, third-party pins, and the update feed.
- [`Vestigo.Installer.Actions`](installer/Vestigo.Installer.Actions) — net48 MSI custom actions and the pure listen-settings logic shared with the tests.
- [`Vestigo.Installer`](installer) — WixSharp project that builds the per-machine x64 MSI and Windows service.
- [`tests/Vestigo.Server.Tests`](tests/Vestigo.Server.Tests) — path, authentication, capture, module, installer, and documentation tests.

## Security

- Tokens are compared by SHA-256 digest and are never written to logs.
- Trace ETL, PDB, TMF, zip, and conversion-output paths stay inside `AllowedInputRoots` or `AllowedOutputRoots`, which default to the protected `<settings>\traces` folder. Remote agents can resolve PDBs from installed binaries or upload them to the `traces` named root.
- File, log, process, and device paths use named `Vestigo:Roots` (and optional `VolumeRoots`) with `Read`, `Write`, and `Execute`.
- `Processes`, `Devices`, `RemoteDebugger`, `Desktop`, `Audio`, and `DebugOutput` stay off until their module is enabled. Silent uninstall, scheduled-task changes, screenshots, default-audio switching, boot-configuration writes, UAC changes, and silent self-update have separate gates (`Software:AllowUninstall`, `ScheduledTasks:AllowChanges`, `Desktop:AllowScreenshots`, `Audio:AllowSetDefault`, `System:AllowBootConfigurationChanges`, `System:AllowUacChanges`, `Updates:AllowSelfUpdate`). Enabling those without tokens is allowed and logs a startup `SECURITY WARNING`.
- Output files are not overwritten unless the tool argument says so.
- Event count, input size, output size, capture duration, job output, and spool retention are bounded.

See [docs/security.md](docs/security.md) and [docs/configuration.md](docs/configuration.md).

## Build and installer

Windows publish output is framework-dependent, ReadyToRun, and `win-x64`:

```powershell
.\build.ps1 PublishLocal
```

On Windows, with WiX 5.0.2, `WixToolset.UI.wixext/5.0.2`, `WixToolset.Firewall.wixext/5.0.2`, `WixToolset.Netfx.wixext/5.0.2`, and `WixToolset.Util.wixext/5.0.2`:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Firewall.wixext/5.0.2
wix extension add -g WixToolset.Netfx.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
.\build.ps1 BuildInstaller
```

`BuildInstaller` needs network access: it downloads the pinned signed [Vicius](https://github.com/nefarius/vicius) 1.20.1 x64 updater, the pinned signed [nefcon](https://github.com/nefarius/nefcon) 1.21.0 CLI, the pinned [DebugView++](https://github.com/CobaltFusion/DebugViewPP) 1.30.0.0 `DebugViewConsole.exe`, and the pinned [ripgrep](https://github.com/BurntSushi/ripgrep) 15.2.0 x64 archive. It verifies each archive or asset hash (and the Vicius and nefcon Authenticode signatures) before packaging them with the server. nefcon, DebugViewConsole and `rg.exe` are installed under `tools\`.

The MSI is `publish-x64\installer\Vestigo-v<version>.msi`. Its access and authentication pages choose localhost or any address and whether to require an API token (any address with a token is the default) and write those choices to the ProgramData `appsettings.json`. File-system pages then choose `VolumeRoots` and `VolumeRootAccess` (fixed volumes with Read, Write, and Execute by default). A feature tree selects automatic updates (installed and registered for the installing user by default; deselect to skip) and the modules to enable, independent of the listen mode (a no-token server with powerful modules is allowed and logs a startup warning); see [docs/windows-service.md](docs/windows-service.md) for the silent-install properties. A local build is unsigned. Tagging `v*` signs `Vestigo.exe`, `Vestigo.dll`, and the MSI through SignRelay, then publishes `updates.json` and `updates.json.minisig` beside the MSI on Buildbot. If `LICENSE` changes, update `installer\License.rtf` as well. Third-party terms for the updater, nefcon, and DebugView++ are in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

## Limitations

- Windows only. The NT Kernel Logger and kernel-mode realtime providers are not supported.
- Vestigo decodes events; it does not record a raw ETL file.
- There is no server push. Follow a live capture by long-polling `read_capture` or `read_debug_output` with `waitSeconds`; external sessions can be read with `attach_etw_session` once allowed by `Vestigo:AttachableSessions`.
- Debug-output capture needs an exclusive `DBWIN_BUFFER` reader. Another DebugView instance fails the start. Debugger-attached processes and kernel `DbgPrint` are not captured.
- There is no management UI. `/` is a static page that only explains how to connect an MCP client. Traffic is plain HTTP; use a trusted network or a TLS-terminating proxy when the server is reachable beyond the machine.
- Screenshots fail with `desktop_unavailable` on the lock screen and the UAC secure desktop. DRM, `SetWindowDisplayAffinity`, and some GPU or fullscreen-exclusive windows may capture black.
- BCD test-signing applies only to the default boot entry, takes effect after a reboot, and is refused while Secure Boot is on or its state is unknown.
- Turning UAC off (`set_uac` `disabled`) takes effect after a reboot and breaks UWP and Store apps until UAC is turned back on.
- WPP enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` remain numeric in the upstream decoder.
- Symbol resolution uses the released `Nefarius.Utilities.ETW` package.
- Silent uninstall covers MSI, Burn, Inno Setup, NSIS and entries that publish a `QuietUninstallString`. Other installers are refused, per-user entries cannot be uninstalled, and AppX/MSIX packages are not listed.
- Scheduled task tools work on the local machine only. They cannot create or edit tasks, and task history needs the `Microsoft-Windows-TaskScheduler/Operational` channel to be enabled.
- Audio default-device switching uses the undocumented `IPolicyConfig` interface and may fail on a future Windows release. Per-app device overrides are not shown or changed. The peak meter is a short sample, not a continuous monitor. The service context does not see user audio sessions.
- `search_text` runs the bundled ripgrep with a bounded result count, line length, run time and concurrency, so a search can stop early with `truncated` or `timedOut` set. It does not follow links or junctions, skips binary files, and decodes only UTF-8 and UTF-16 with a byte order mark. It looks at files as they are on disk and cannot follow a growing file; use `read_text_file_since` for that.
- Vestigo never triggers a reboot; tools report or schedule reboot-time work.
- `check_for_updates` uses the service account's proxy settings. `start_self_update` needs the MSI Automatic updates feature; after Vicius hands off to its temporary copy the install result is only in the `%ProgramData%\...\Vestigo\updates` log until the service is back.

## Documentation

- [Project site](https://nefarius.github.io/Vestigo/)
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
- [Nefarius.Utilities.WindowsVersion](https://github.com/nefarius/Nefarius.Utilities.WindowsVersion)
- [NAudio](https://github.com/naudio/NAudio)
- [CsWin32](https://github.com/microsoft/CsWin32)
- [Model Context Protocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [Serilog](https://github.com/serilog/serilog)
- [CliWrap](https://github.com/Tyrrrz/CliWrap)
- [MinVer](https://github.com/adamralph/minver)
- [WixSharp](https://github.com/oleg-shilo/wixsharp)
- [WiX Toolset](https://wixtoolset.org/)
- [Vicius](https://github.com/nefarius/vicius)
- [nefcon](https://github.com/nefarius/nefcon)
- [DebugView++](https://github.com/CobaltFusion/DebugViewPP)
- [ripgrep](https://github.com/BurntSushi/ripgrep)
