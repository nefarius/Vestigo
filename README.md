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

The server listens on loopback only. `/health` is anonymous. `/mcp` is anonymous until `ApiTokens` contains one or more bearer tokens.

## Projects

- [`Vestigo.Server`](server) â€” ASP.NET Core host, MCP tools, and capture manager.
- [`Vestigo.Installer`](installer) â€” WixSharp project that builds the per-machine x64 MSI and Windows service.
- [`tests/Vestigo.Server.Tests`](tests/Vestigo.Server.Tests) â€” path, authentication, capture, and package-boundary tests.

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

On Windows, with WiX 5.0.2 and `WixToolset.UI.wixext/5.0.2`:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
.\build.ps1 BuildInstaller
```

The MSI is `publish-x64\installer\Vestigo.msi`. `BuildInstaller` downloads the pinned signed [Vicius](https://github.com/nefarius/vicius) 1.20.0 x64 updater and packages it with the server. A local build is unsigned. Tagging `v*` signs `Vestigo.exe`, `Vestigo.dll`, and the MSI through SignRelay, then publishes `updates.json` and `updates.json.minisig` beside the MSI on Buildbot. If `LICENSE` changes, update `installer\License.rtf` as well. Third-party terms for the updater are in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

## Limitations

- Windows only. The NT Kernel Logger and kernel-mode realtime providers are not supported.
- Vestigo decodes events; it does not record a raw ETL file.
- WPP enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` remain numeric in the upstream decoder.
- Symbol resolution uses the released `Nefarius.Utilities.ETW` package.

## Documentation

- [Architecture](docs/architecture.md)
- [MCP tools](docs/tools.md)
- [Configuration](docs/configuration.md)
- [Security](docs/security.md)
- [Windows service](docs/windows-service.md)
- [Symbols](docs/symbols.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Release](docs/release.md)
- [API reference](docs/api/index.md)

## Sources & 3rd party credits

- [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW)
- [Nefarius.Shared.PdbUtils](https://github.com/nefarius/PdbUtils)
- [Model Context Protocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [Serilog](https://github.com/serilog/serilog)
- [MinVer](https://github.com/adamralph/minver)
- [WixSharp](https://github.com/oleg-shilo/wixsharp)
- [WiX Toolset](https://wixtoolset.org/)
- [Vicius](https://github.com/nefarius/vicius)
