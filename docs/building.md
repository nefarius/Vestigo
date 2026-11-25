# Building

Local builds run on Windows. The publish output is framework-dependent, ReadyToRun, and `win-x64`. Tagged signing, the update feed, and the CI pipeline are in [release](release.md). Installer pages, the service account, and silent-install properties are in [Windows service](windows-service.md).

## Prerequisites

Running from source needs Windows 10 or later, the .NET 10 SDK, and an MCP client that speaks Streamable HTTP:

```powershell
dotnet run --project server
```

`dotnet run` uses the repository `appsettings.json`, which defaults to loopback without tokens. It does not need a separate runtime install, because the SDK already includes the .NET and ASP.NET Core runtimes.

Publishing and the MSI need the same SDK. `BuildInstaller` also needs:

- Visual Studio MSBuild with the MSVC v143 toolset. `BuildCredentialProvider` builds `credential-provider/Vestigo.CredentialProvider.vcxproj`, and `PublishInputHelper` publishes the Native AOT `Vestigo.InputHelper.exe` with the MSVC x64 toolset.
- WiX CLI 5.0.2 and the matching extensions below.
- Network access. The installer build downloads pinned third-party tools and verifies them before packaging.

An installed MSI, as opposed to `dotnet run`, needs the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 on the target machine. See [Windows service](windows-service.md).

## Publish

```powershell
.\build.ps1 PublishLocal
```

## Installer

Install WiX, then build the MSI:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Netfx.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
.\build.ps1 BuildInstaller
```

`BuildInstaller` publishes the server, publishes the Native AOT `Vestigo.InputHelper.exe`, builds the Credential Provider, and downloads:

- the pinned signed [Vicius](https://github.com/nefarius/vicius) 1.20.1 x64 updater
- the pinned signed [nefcon](https://github.com/nefarius/nefcon) 1.21.0 CLI
- the pinned [DebugView++](https://github.com/CobaltFusion/DebugViewPP) 1.30.0.0 `DebugViewConsole.exe`
- the pinned [ripgrep](https://github.com/BurntSushi/ripgrep) 15.2.0 x64 archive

It verifies each archive or asset hash, and the Vicius and nefcon Authenticode signatures, before packaging them with the server. nefcon, DebugViewConsole, and `rg.exe` are installed under `tools\`.

The MSI is `publish-x64\installer\Vestigo-v<version>.msi`. Its access and authentication pages choose localhost or any address and whether to require an API token. Any address with a token is the default. Those choices are written to the ProgramData `appsettings.json`. File-system pages then choose `VolumeRoots` and `VolumeRootAccess` (fixed volumes with Read, Write, and Execute by default). A feature tree selects automatic updates (installed and registered for the installing user by default; deselect to skip) and the modules to enable, independent of the listen mode. A no-token server with powerful modules is allowed and logs a startup warning.

A local build is unsigned. Tagging `v*` signs `Vestigo.exe`, `Vestigo.dll`, `Vestigo.CredentialProvider.dll`, `Vestigo.InputHelper.exe`, and the MSI through SignRelay, then publishes `updates.json` and `updates.json.minisig` beside the MSI on Buildbot. See [release](release.md).

If `LICENSE` changes, update `installer\License.rtf` as well. Third-party terms for the updater, nefcon, DebugView++, ripgrep, and the bundled NuGet packages (including MudBlazor) are in [installer/THIRD-PARTY-NOTICES.txt](../installer/THIRD-PARTY-NOTICES.txt).

## Tests and generated docs

```powershell
dotnet test -c Release
```

`docs/generated/configuration.md` and `docs/generated/mcp-tools.md` are produced from the option defaults and MCP tool attributes. After changing either, regenerate the catalogs and then confirm the checked-in files match:

```powershell
$env:VESTIGO_REGENERATE_DOCS = '1'
dotnet test -c Release
Remove-Item Env:VESTIGO_REGENERATE_DOCS
dotnet test -c Release
```

The net48 custom-action package check builds the installer actions, extracts the `.CA.dll` CAB, and exercises settings reads and merges on .NET Framework without installing an MSI:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Verify-InstallerCustomAction.ps1 -Configuration Release
```
