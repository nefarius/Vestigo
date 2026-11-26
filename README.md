# <img src="assets/NSS-128x128.png" align="left" />Vestigo

[![.NET](https://github.com/nefarius/Vestigo/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/Vestigo/actions/workflows/build.yml)
![Requirements](https://img.shields.io/badge/Requires-.NET%2010-blue.svg)
![Windows only](https://img.shields.io/badge/Windows-10%2B-red)
[![Assisted by Cursor AI](https://img.shields.io/badge/Assisted%20by-Cursor%20AI-8B5CF6?style=flat)](https://cursor.com/)

Windows MCP server for driver and application development. It runs as a Windows service and hosts a stateless Streamable HTTP endpoint at `/mcp`.

## Summary

A remote agent uses Vestigo to convert WPP traces, capture ETW, move files, and operate a Windows lab PC: processes, devices, the desktop, and machine state. Trace decoding uses [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW) with PDB or TMF files, resolved from an installed binary, the local cache, a configured symbol server, or the built-in `traces` folder. A fresh MSI binds every interface and writes a generated bearer token. `dotnet run` uses the repository `appsettings.json` and stays on loopback without tokens until you add them. Settings are edited in the browser at `http://localhost:5098/config`. Trusted .NET plugins are opt-in. Guides for developers and QA are on the [project site](https://nefarius.github.io/Vestigo/).

## Quick start

1. Install the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0. The MSI refuses to continue without them. The [ASP.NET Core 10.0 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0) includes both, or run this from an elevated prompt:

   ```powershell
   winget install --id Microsoft.DotNet.Runtime.10 --architecture x64
   winget install --id Microsoft.DotNet.AspNetCore.10 --architecture x64
   ```

   `--architecture x64` matters on Windows 11 ARM64, because a native ARM64 runtime does not satisfy the MSI. `dotnet run` does not need this step; the .NET 10 SDK already includes both runtimes.
2. Install the MSI, or run `dotnet run --project server`.
3. After a default MSI install, copy the generated `ApiTokens` entry from `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. The same settings can be edited at `http://localhost:5098/config`. `dotnet run` stays anonymous until you add tokens.
4. Point an MCP client at `http://127.0.0.1:5098/mcp`. When `ApiTokens` is non-empty, send `Authorization: Bearer <token>`. Opening `http://127.0.0.1:5098/` shows the same connection instructions.

The default MSI listen mode is any address with a token (`[::]:5098`, dual-stack IPv4 and IPv6). Loopback still works on the same machine. `/health` is always anonymous. Binding beyond loopback without tokens logs a startup warning. File-transfer tools return short-lived `/files` URLs, and `take_screenshot` with `share: true` returns its own preview URL. Endpoint and ticket details are in [server/README.md](server/README.md). Listen modes, the firewall rule, and silent install are in [Windows service](docs/windows-service.md).

## Features

- **Tracing.** Offline WPP conversion and realtime ETW capture, including attaching to an allow-listed external session.
- **Files, logs, and search.** Root-confined transfer, log tails, ripgrep search, JSON queries, and Windows event logs.
- **Services, tasks, software, and jobs.** Service and driver control, scheduled tasks, software inventory with gated silent uninstall, and background jobs.
- **Processes, devices, and debugging.** Process launch, device and driver management, HID inspection, Win32 debug output, and the Visual Studio remote debugger.
- **Desktop, perception, input, and audio.** Windows, displays, and gated screenshots; screen description with UI Automation, Windows OCR, and [OpenCV](https://opencv.org/); gated mouse and keyboard input; default audio switching.
- **System, diagnostics, performance, and servicing.** Boot security, UAC, reboot, AutoLogon, interactive logon, power, allow-listed registry reads, PE and signature inspection, probes and dumps, machine-wide sockets and TCP EStats, opt-in machine and VM performance analysis (PDH counters, storage topology, minifilters, network adapters and NDIS/WFP filters, kernel ETW summaries including a network group, WPR `.etl`, storage probes, and drop-only pktmon), and read-only Windows Update diagnostics.
- **Configuration and updates.** The localhost settings page, and a signed self-update through the packaged updater.
- **External plugins.** Opt-in .NET modules, MCP tools, and authenticated HTTP routes. See [plugins](docs/plugins.md).

Tool contracts are in [MCP tools](docs/tools.md). The generated parameter catalog is [docs/generated/mcp-tools.md](docs/generated/mcp-tools.md).

## Requirements

Installed MSI:

- Windows 10 or later (x64). Windows 11 on ARM64 is supported through x64 emulation.
- The x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 (compatible 10.0.x patches; the highest Microsoft.NETCore.App patch must be at least the highest Microsoft.AspNetCore.App patch). On ARM64, install the x64 runtimes under `%ProgramFiles%\dotnet\x64`.
- An elevated service account for realtime ETW sessions and most mutating tools.

From source: Windows 10 or later, the .NET 10 SDK, and an MCP client that speaks Streamable HTTP. Which calls need elevation or a logged-on console user is in [limitations](docs/limitations.md#elevation-and-session-context). Installer requirements are in [Windows service](docs/windows-service.md), and the local build is in [building](docs/building.md).

## Security

- Tokens are compared by SHA-256 digest and are never written to logs.
- Trace, file, log, process, and device paths stay inside configured roots. Empty trace-root lists use the protected `traces` folder.
- Powerful modules and individual actions stay off until they are enabled. Turning one on without a token is allowed and logs a startup `SECURITY WARNING`.
- `/config` serves only a direct loopback client. Local users can read credentials and change privileged settings without an API token. Never proxy that page or its Blazor transport.
- Traffic is plain HTTP. Event count, transfer size, capture duration, job output, and spool retention are bounded. Existing output files stay in place unless the tool argument says to overwrite them.

Gates, root flags, and plugin trust are in [security](docs/security.md) and [configuration](docs/configuration.md).

## Repository layout

| Path | Description |
| --- | --- |
| [server/](server) | ASP.NET Core host, MCP tools, capture manager, and feature modules |
| [server/Vestigo.Abstractions/](server/Vestigo.Abstractions) | Public plugin contracts (`Vestigo.Extensibility`) |
| [input-helper/](input-helper) | Native AOT `send_input` helper |
| [credential-provider/](credential-provider) | Native x64 Credential Provider used by interactive logon and unlock |
| [installer/](installer) | WixSharp project for the per-machine x64 MSI and Windows service |
| [installer/Vestigo.Installer.Actions/](installer/Vestigo.Installer.Actions) | net48 MSI custom actions and settings-merge logic shared with the tests |
| [build/](build) | Fallout build: `build.ps1` targets, third-party pins, and the update feed |
| [examples/Vestigo.ExamplePlugin/](examples/Vestigo.ExamplePlugin) | Example external plugin |
| [tests/Vestigo.Server.Tests/](tests/Vestigo.Server.Tests) | Path, authentication, capture, module, installer, and documentation tests |
| [docs/](docs) | Narrative docs and generated catalogs |
| [site/](site) | GitHub Pages booklet |

## Building

```powershell
.\build.ps1 PublishLocal
.\build.ps1 BuildInstaller
```

`PublishLocal` writes the framework-dependent `win-x64` server. `BuildInstaller` also builds the Credential Provider and the input helper, then packages the pinned updater and tools. WiX, the MSVC toolset, and tests are in [building](docs/building.md). Signing and the release feed are in [release](docs/release.md).

## Limitations

- Windows only. The Trace module decodes events and does not record a raw ETL file or use the NT Kernel Logger. The opt-in Performance module can capture a bounded kernel-latency summary (`AllowKernelTrace`), a gated WPR `.etl` (`AllowRawEtlCapture`), and a gated drop-only pktmon session (`AllowPacketDropCapture`).
- The MCP endpoint is stateless. There is no server push; live output is a long-poll.
- External plugins are off until an administrator opts in. They run in-process with the service, and loading one requires a restart.
- Per-tool bounds, session requirements, and refusal cases are in [limitations](docs/limitations.md).

## Documentation

- [Project site](https://nefarius.github.io/Vestigo/)
- [Architecture](docs/architecture.md)
- [MCP tools](docs/tools.md)
- [Limitations](docs/limitations.md)
- [Building](docs/building.md)
- [Windows servicing diagnostics](docs/windows-servicing.md)
- [External plugins](docs/plugins.md)
- [MCP tool catalog](docs/generated/mcp-tools.md)
- [Configuration](docs/configuration.md)
- [Configuration defaults](docs/generated/configuration.md)
- [Security](docs/security.md)
- [Windows service](docs/windows-service.md)
- [Symbols](docs/symbols.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Release](docs/release.md)

## Licensing

Vestigo is [MIT](LICENSE) licensed. Third-party software shipped in the MSI, and its redistribution terms, are in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

## Sources & 3rd party credits

The notices file is also installed with Vestigo. The links below acknowledge libraries and tools used to build or run Vestigo; this list does not replace their license notices.

- **Tracing and Windows:** [Nefarius.Utilities.ETW](https://github.com/nefarius/Nefarius.Utilities.ETW), [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (MIT; kernel ETW summaries and offline `.etl` analysis), [WmiLight](https://github.com/MartinKuschnik/WmiLight) (MIT; CIM storage and VM inventory), [Nefarius.Utilities.DeviceManagement](https://github.com/nefarius/Nefarius.Utilities.DeviceManagement), [Nefarius.Utilities.WindowsVersion](https://github.com/nefarius/Nefarius.Utilities.WindowsVersion), [NAudio](https://github.com/naudio/NAudio), [PeNet](https://github.com/secana/PeNet), [CsWin32](https://github.com/microsoft/CsWin32), [ManagedDism](https://github.com/jeffkl/ManagedDism) (`Microsoft.Dism`, MIT)
- **Protocol, UI, and perception:** [Model Context Protocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/), [MudBlazor](https://mudblazor.com/) (MIT; local configuration page), [json-everything](https://github.com/json-everything/json-everything) (`JsonPath.Net`), [OpenCvSharp](https://github.com/shimat/opencvsharp) (Apache-2.0), [OpenCV](https://opencv.org/)
- **Host, build, and updates:** [Serilog](https://github.com/serilog/serilog), [CliWrap](https://github.com/Tyrrrz/CliWrap), [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) (MIT; STUN CRC32 fingerprints), [System.Text.Json](https://www.nuget.org/packages/System.Text.Json) (MIT; installer settings parsing and serialization), [minisign-net](https://www.nuget.org/packages/minisign-net) (update manifest signatures), [Nefarius.Vicius.Abstractions](https://www.nuget.org/packages/Nefarius.Vicius.Abstractions), [MinVer](https://github.com/adamralph/minver), [WixSharp](https://github.com/oleg-shilo/wixsharp), [WiX Toolset](https://wixtoolset.org/), [Vicius](https://github.com/nefarius/vicius), [nefcon](https://github.com/nefarius/nefcon), [DebugView++](https://github.com/CobaltFusion/DebugViewPP), [ripgrep](https://github.com/BurntSushi/ripgrep)
