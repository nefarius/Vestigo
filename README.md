# <img src="assets/NSS-128x128.png" align="left" />Vestigo

[![.NET](https://github.com/nefarius/Vestigo/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/Vestigo/actions/workflows/build.yml)
![Requirements](https://img.shields.io/badge/Requires-.NET%2010-blue.svg)
![Windows only](https://img.shields.io/badge/Windows-8.0+-red)
[![Assisted by Cursor AI](https://img.shields.io/badge/Assisted%20by-Cursor%20AI-8B5CF6?style=flat)](https://cursor.com/)

Vestigo is a localhost MCP server for offline conversion and realtime capture of Windows driver traces. It decodes WPP events with [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW), using PDB or TMF files.

## Requirements

- Windows 8 or later and the .NET 10 runtime
- An elevated service account for starting realtime ETW sessions
- MCP client support for Streamable HTTP

Reading an existing ETL file does not by itself require elevation. Creating a realtime session does.

## Quick start

1. Install the MSI or run `dotnet run --project server`.
2. Edit `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`.
3. Point an MCP client at `http://127.0.0.1:5098/mcp`.

The server listens on loopback by default and can be bound to any IP address with `ListenAddress`; configure `ApiTokens` before exposing it (a startup warning is logged otherwise). `/health` is anonymous. `/mcp` is anonymous until `ApiTokens` contains one or more bearer tokens.

## Projects

- [`Vestigo.Server`](server) — ASP.NET Core host, MCP tools, and capture manager.
- [`Vestigo.Installer.Actions`](installer/Vestigo.Installer.Actions) — net48 MSI custom actions and the pure listen-settings logic shared with the tests.
- [`Vestigo.Installer`](installer) — WixSharp project that builds the per-machine x64 MSI and Windows service.
- [`tests/Vestigo.Server.Tests`](tests/Vestigo.Server.Tests) — path, authentication, capture, and package-boundary tests.

## Security

- Tokens are compared by SHA-256 digest and are never written to logs.
- Client paths must fall inside `AllowedInputRoots` or `AllowedOutputRoots`.
- Output files are not overwritten unless the tool argument says so.
- Event count, input size, output size, capture duration, and spool retention are bounded.

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

The MSI is `publish-x64\installer\Vestigo.msi`. Its listen/security page offers four modes (localhost or any address, each with or without an API token; any address with a token is the default) and writes them to the ProgramData `appsettings.json`. A feature tree then selects the modules to enable, independent of the listen mode (a no-token server with powerful modules is allowed and logs a startup warning); see [docs/windows-service.md](docs/windows-service.md) for the silent-install properties. `BuildInstaller` downloads the pinned signed [Vicius](https://github.com/nefarius/vicius) 1.20.0 x64 updater and the pinned signed [nefcon](https://github.com/nefarius/nefcon) 1.21.0 CLI (installed under `tools\`) and packages them with the server. A local build is unsigned. Tagging `v*` signs `Vestigo.exe`, `Vestigo.dll`, and the MSI through SignRelay, then publishes `updates.json` and `updates.json.minisig` beside the MSI on Buildbot. If `LICENSE` changes, update `installer\License.rtf` as well. Third-party terms for the updater and nefcon are in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

## Limitations

- Windows only. The NT Kernel Logger and kernel-mode realtime providers are not supported.
- Vestigo decodes events; it does not record a raw ETL file.
- There is no server push. Follow a live capture by long-polling `read_capture` with `waitSeconds`; external sessions can be read with `attach_etw_session` once allowed by `Vestigo:AttachableSessions`.
- WPP enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` remain numeric in the upstream decoder.
- Symbol resolution uses the released `Nefarius.Utilities.ETW` package.
- Silent uninstall covers MSI, Burn, Inno Setup, NSIS and entries that publish a `QuietUninstallString`. Other installers are refused, per-user entries cannot be uninstalled, and AppX/MSIX packages are not listed.

## Documentation

- [Architecture](docs/architecture.md)
- [MCP tools](docs/tools.md)
- [Configuration](docs/configuration.md)
- [Security](docs/security.md)
- [Windows service](docs/windows-service.md)
- [Symbols](docs/symbols.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Release](docs/release.md)

## Sources & 3rd party credits

- [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW)
- [Nefarius.Shared.PdbUtils](https://github.com/nefarius/PdbUtils)
- [Model Context Protocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [Serilog](https://github.com/serilog/serilog)
- [MinVer](https://github.com/adamralph/minver)
- [WixSharp](https://github.com/oleg-shilo/wixsharp)
- [WiX Toolset](https://wixtoolset.org/)
- [Vicius](https://github.com/nefarius/vicius)
