# Release

Version numbers come from MinVer and `v*` tags. MinVer's default CLR `AssemblyVersion` is `{major}.0.0.0`; Vestigo overrides that after MinVer to `{major}.{minor}.{patch}.0` so a `v2.1.0` build advertises `2.1.0.0` to MCP clients. `FileVersion` remains `{major}.{minor}.{patch}.0`, and `InformationalVersion`/`Version` remain the MinVer SemVer. The MSI product version is read from the published `Vestigo.exe`. The installer writes `Vestigo-v<product-version>.msi`.

## CI pipeline

The workflow (`.github/workflows/build.yml`) builds the app once and hands the result to the installer job:

`security`, `osv-restore`, and `build` run in parallel, then `osv` and `build-installer` -> `release` (tags only)

- `security` (Ubuntu) runs Gitleaks against git history and the checked-out tree, then Semgrep (`p/csharp`). `.gitleaks.toml` allowlists the fake installer test token. Semgrep uses a digest-pinned image.
- `osv-restore` (Windows) writes NuGet lock files for the net10 projects and the net48 custom-action project. `osv` (Ubuntu) scans those lock files with a digest-pinned OSV Scanner image. A finding fails the job, and `build-installer` needs `security` and `osv`, so secrets, SAST, and dependency advisories block the MSI and release. The DTF packages stay on the frozen WiX 5.0.2 set.
- `build` (Windows) runs the tests, then `.\build.ps1 PublishLocal PrepareUpdater PrepareNefcon PrepareDebugView`, and uploads `publish-x64/` (`server`, `updater`, `tools`) as the `vestigo-publish-x64` artifact (3-day retention).
- `build-installer` (Windows, `needs: [build, security, osv]`) downloads that artifact into `publish-x64`, installs WiX, and builds the MSI with `.\build.ps1 BuildInstaller --skip PublishLocal PrepareUpdater PrepareNefcon PrepareDebugView`. `BuildInstaller` only runs the installer project (`dotnet run` on `installer/`, which also builds the net48 custom actions); the skipped targets are its only `DependsOn`, so the server is never rebuilt or republished in that job. Locally, `.\build.ps1 BuildInstaller` without `--skip` still publishes and stages everything.
- `release` (tags only) publishes the signed files.

Pull requests and `master` build an unsigned MSI as a smoke test and do not upload it.

A `v*` tag, in `build-installer`, using the unsigned payload from the artifact:

1. Checks that `Vestigo.exe` and `Vestigo.dll` are unsigned.
2. Signs both with SignRelay.
3. Builds `publish-x64\installer\Vestigo-v<product-version>.msi` from those signed binaries.
4. Signs and verifies the MSI.
5. Writes `updates.json` for that signed MSI and creates `updates.json.minisig` with the existing Nefarius minisign key.
6. Verifies the sidecar against the public key embedded in Vicius 1.20.1.
7. Uploads `Vestigo-v<product-version>.msi`, `updates.json`, and `updates.json.minisig` as one artifact and posts the Buildbot webhook.
8. Publishes a GitHub Release for the tag, with generated notes and those three files attached. Installed clients do not download the GitHub copies.

The tag pipeline requires `SIGN_RELAY_SERVER`, `SIGN_RELAY_CI_TOKEN`, `WEBHOOK_URL`, `MINISIGN_PRIVATE_KEY`, and `MINISIGN_PASSWORD`. `MINISIGN_PRIVATE_KEY` is the minisign private-key file contents. The workflow writes it to a temporary file, passes that path as `VESTIGO_MINISIGN_KEY`, and deletes the file. It does not print the key or passphrase.

`manifestVersion` is `github.run_id`. It must increase for every published feed. Vicius stores the last accepted value per user and rejects a lower one. Re-running the same Actions run keeps the same value.

Buildbot publishes the three files together under `https://buildbot.nefarius.at/builds/Vestigo/latest/`. The updater requests only `updates.json` and `updates.json.minisig` from that directory, then downloads `Vestigo-v<product-version>.msi` from the URL in the signed manifest. A check that races a symlink update fails the MSI checksum and retries on the next run.

WiX is pinned to CLI `5.0.2`, `WixToolset.UI.wixext/5.0.2`, and `WixToolset.Firewall.wixext/5.0.2` (install each with `wix extension add -g <name>/5.0.2`). Dependabot ignores `WixToolset.Dtf.CustomAction` and `WixToolset.Dtf.WindowsInstaller` (both `5.0.2`) and `WixSharp_wix4` (`2.14.2`), including security updates, because that set is proven compatible and later WiX majors are not a drop-in replacement. The installer also builds `installer/Vestigo.Installer.Actions` (net48, WiX DTF 5.0.2): `dotnet run` on the installer builds it first and embeds `Vestigo.Installer.Actions.CA.dll` in the MSI, so no extra release step is needed. The WixSharp project GUID is `7c2e1a44-6b8f-4d3a-9e15-2f6a8c0d4b71`. The MSI product code is generated for each build. The upgrade code is `9a4d6e28-1c7b-4f05-8d3e-5b2c7a9f0e64`. A later MSI with that upgrade code replaces an earlier Vestigo installation. The installer stops when the x64 .NET 10 ASP.NET Core runtime is missing.

Keep `installer/License.rtf` aligned with `LICENSE`.

## Generated documentation

`docs/generated/configuration.md` and `docs/generated/mcp-tools.md` are produced from `ConfigurationCatalog` and `McpToolCatalog`. After changing options or MCP tool attributes, regenerate them by running the test suite with `VESTIGO_REGENERATE_DOCS=1`, then run the suite again without that variable so the checked-in files match.

`BuildInstaller` downloads the pinned Vicius 1.20.1 x64 archive, checks its SHA-256, and requires the extracted executable to have a valid Authenticode signature for `Nefarius Software Solutions e.U.` before the MSI packages it as `Nefarius_Vestigo_Updater.exe`. The default-selected `Updater` feature installs that executable and `Nefarius_Vestigo_Updater.json`; `THIRD-PARTY-NOTICES.txt` always installs with the server. Deselecting the feature, or `REMOVE=Updater`, omits the updater payload and registration.

The manifest's release version is the MSI `ProductVersion`. When the updater feature is installed, the same version is written to `HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version`. Vicius compares that registry value with the signed release version.

## Update smoke test

On a Windows machine with the x64 .NET 10 ASP.NET Core runtime:

- Install an older signed Vestigo MSI with the updater selected (the default), then confirm `Nefarius_Vestigo_Updater.exe --install --silent --override-success-code 0` exits 0 and creates the current user's logon autostart entry and daily scheduled task.
- Launch the updater and confirm it offers the newer Buildbot release, shows the MSI summary, and requests elevation.
- Accept the update. Confirm the service stops and starts, the product version and registry value advance, and an in-flight capture has ended.
- Cancel the MSI once and confirm Vestigo remains on the previous version.
- Repeat an update that returns MSI exit code 3010 and confirm the updater treats it as success and asks for a restart.
- Install with the updater deselected (or `REMOVE=Updater`) and confirm the updater files, version registry value, autostart entry, and scheduled task are absent while the service still starts.
- Use Modify to add the updater and confirm the files appear and the current user is registered; then deselect it and confirm deregistration runs and the files are removed.
- Major-upgrade once with the updater still selected and once with it deselected. Confirm a selected upgrade re-registers the current user, and a deselected upgrade leaves no autostart entry or scheduled task.
- Uninstall Vestigo and confirm the autostart entry and scheduled task are gone.

## nefcon pin

`PrepareNefcon` (a dependency of `BuildInstaller`, run by the `build` CI job) downloads the pinned nefcon release archive, checks its SHA-256, extracts `x64\nefconc.exe` to `publish-x64\tools\`, and requires a valid Authenticode signature for `Nefarius Software Solutions e.U.`. The MSI installs it as `tools\nefconc.exe` with `tools\nefcon-LICENSE.txt`; the MIT notice is also in `installer\THIRD-PARTY-NOTICES.txt`.

To bump nefcon manually:

1. Download the new `nefcon_vX.Y.Z.zip` from the [nefcon releases](https://github.com/nefarius/nefcon/releases) (`gh release download vX.Y.Z -R nefarius/nefcon -p 'nefcon_vX.Y.Z.zip'`).
2. Compute its SHA-256 (`Get-FileHash`) and confirm `Get-AuthenticodeSignature` on `x64\nefconc.exe` reports a valid Nefarius signature.
3. Update `Version`, `ArchiveSha256`, and `ArchiveUrl` in `build/NefconPin.cs`, and the version in `installer\THIRD-PARTY-NOTICES.txt` and `installer\nefcon-LICENSE.txt` if the license changed.
4. Run `.\build.ps1 PrepareNefcon` and the test suite.

## DebugView++ pin

`PrepareDebugView` (a dependency of `BuildInstaller`, run by the `build` CI job) downloads the pinned DebugView++ `DebugViewConsole.exe` asset, checks its SHA-256, and stages it to `publish-x64\tools\`. There is no Authenticode publisher check: the binary is third-party. The MSI installs it as `tools\DebugViewConsole.exe` with `tools\DebugViewPP-LICENSE.txt`; the Boost notice is also in `installer\THIRD-PARTY-NOTICES.txt`. Vestigo re-checks the same digest before every launch.

To bump DebugViewConsole manually:

1. Download `DebugViewConsole.exe` from the [DebugView++ releases](https://github.com/CobaltFusion/DebugViewPP/releases) (`gh release download 1.30.0.0 -R CobaltFusion/DebugViewPP -p DebugViewConsole.exe`).
2. Compute its SHA-256 (`Get-FileHash`).
3. Update `Version`, `Sha256`, and `AssetUrl` in `build/DebugViewPin.cs`, `DebugOutputOptions.DefaultExpectedSha256` in `server/DebugOutput/DebugOutputOptions.cs`, and the version in `installer\THIRD-PARTY-NOTICES.txt` and `installer\DebugViewPP-LICENSE.txt` if the license changed.
4. Run `.\build.ps1 PrepareDebugView` and the test suite.
