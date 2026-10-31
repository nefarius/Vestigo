# Release

Version numbers come from MinVer and `v*` tags. The MSI product version is read from the published `Vestigo.exe`.

## CI pipeline

The workflow (`.github/workflows/build.yml`) builds the app once and hands the result to the installer job:

`build` -> `build-installer` -> `release` (tags only)

- `build` (Windows) runs the tests, then `.\build.ps1 PublishLocal PrepareUpdater PrepareNefcon`, and uploads `publish-x64/` (`server`, `updater`, `tools`) as the `vestigo-publish-x64` artifact (3-day retention).
- `build-installer` (Windows, `needs: build`) downloads that artifact into `publish-x64`, installs WiX, and builds the MSI with `.\build.ps1 BuildInstaller --skip PublishLocal PrepareUpdater PrepareNefcon`. `BuildInstaller` only runs the installer project (`dotnet run` on `installer/`, which also builds the net48 custom actions); the skipped targets are its only `DependsOn`, so the server is never rebuilt or republished in that job. Locally, `.\build.ps1 BuildInstaller` without `--skip` still publishes and stages everything.
- `release` (tags only) publishes the signed files.

Pull requests and `master` build an unsigned MSI as a smoke test and do not upload it.

A `v*` tag, in `build-installer`, using the unsigned payload from the artifact:

1. Checks that `Vestigo.exe` and `Vestigo.dll` are unsigned.
3. Signs both with SignRelay.
4. Builds `publish-x64\installer\Vestigo.msi` from those signed binaries.
5. Signs and verifies the MSI.
6. Writes `updates.json` for that signed MSI and creates `updates.json.minisig` with the existing Nefarius minisign key.
7. Verifies the sidecar against the public key embedded in Vicius 1.20.0.
8. Uploads `Vestigo.msi`, `updates.json`, and `updates.json.minisig` as one artifact and posts the Buildbot webhook.
9. Publishes a GitHub Release for the tag, with generated notes and those three files attached. Installed clients do not download the GitHub copies.

The tag pipeline requires `SIGN_RELAY_SERVER`, `SIGN_RELAY_CI_TOKEN`, `WEBHOOK_URL`, `MINISIGN_PRIVATE_KEY`, and `MINISIGN_PASSWORD`. `MINISIGN_PRIVATE_KEY` is the minisign private-key file contents. The workflow writes it to a temporary file, passes that path as `VESTIGO_MINISIGN_KEY`, and deletes the file. It does not print the key or passphrase.

`manifestVersion` is `github.run_id`. It must increase for every published feed. Vicius stores the last accepted value per user and rejects a lower one. Re-running the same Actions run keeps the same value.

Buildbot publishes the three files together under `https://buildbot.nefarius.at/builds/Vestigo/latest/`. The updater requests only `updates.json` and `updates.json.minisig` from that directory, then downloads `Vestigo.msi` from the URL in the signed manifest. A check that races a symlink update fails the MSI checksum and retries on the next run.

WiX is pinned to CLI `5.0.2`, `WixToolset.UI.wixext/5.0.2`, and `WixToolset.Firewall.wixext/5.0.2` (install each with `wix extension add -g <name>/5.0.2`). The installer also builds `installer/Vestigo.Installer.Actions` (net48, WiX DTF 5.0.2): `dotnet run` on the installer builds it first and embeds `Vestigo.Installer.Actions.CA.dll` in the MSI, so no extra release step is needed. The WixSharp project GUID is `7c2e1a44-6b8f-4d3a-9e15-2f6a8c0d4b71`. The MSI product code is generated for each build. The upgrade code is `9a4d6e28-1c7b-4f05-8d3e-5b2c7a9f0e64`. A later MSI with that upgrade code replaces an earlier Vestigo installation. The installer stops when the x64 .NET 10 ASP.NET Core runtime is missing.

Keep `installer/License.rtf` aligned with `LICENSE`.

`BuildInstaller` downloads the pinned Vicius 1.20.0 x64 archive, checks its SHA-256, and requires the extracted executable to have a valid Authenticode signature for `Nefarius Software Solutions e.U.` before the MSI packages it as `Nefarius_Vestigo_Updater.exe`. The MSI also installs `Nefarius_Vestigo_Updater.json` and `THIRD-PARTY-NOTICES.txt`.

The manifest's release version is the MSI `ProductVersion`. The same version is written to `HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version`. Vicius compares that registry value with the signed release version.

## Update smoke test

On a Windows machine with the x64 .NET 10 ASP.NET Core runtime:

- Install an older signed Vestigo MSI, then confirm `Nefarius_Vestigo_Updater.exe --install --silent --override-success-code 0` exits 0 and creates the current user's logon autostart entry and daily scheduled task.
- Launch the updater and confirm it offers the newer Buildbot release, shows the MSI summary, and requests elevation.
- Accept the update. Confirm the service stops and starts, the product version and registry value advance, and an in-flight capture has ended.
- Cancel the MSI once and confirm Vestigo remains on the previous version.
- Repeat an update that returns MSI exit code 3010 and confirm the updater treats it as success and asks for a restart.
- Uninstall Vestigo and confirm the autostart entry and scheduled task are gone.

## nefcon pin

`PrepareNefcon` (a dependency of `BuildInstaller`, run by the `build` CI job) downloads the pinned nefcon release archive, checks its SHA-256, extracts `x64\nefconc.exe` to `publish-x64\tools\`, and requires a valid Authenticode signature for `Nefarius Software Solutions e.U.`. The MSI installs it as `tools\nefconc.exe` with `tools\nefcon-LICENSE.txt`; the MIT notice is also in `installer\THIRD-PARTY-NOTICES.txt`.

To bump nefcon manually:

1. Download the new `nefcon_vX.Y.Z.zip` from the [nefcon releases](https://github.com/nefarius/nefcon/releases) (`gh release download vX.Y.Z -R nefarius/nefcon -p 'nefcon_vX.Y.Z.zip'`).
2. Compute its SHA-256 (`Get-FileHash`) and confirm `Get-AuthenticodeSignature` on `x64\nefconc.exe` reports a valid Nefarius signature.
3. Update `Version`, `ArchiveSha256`, and `ArchiveUrl` in `build/NefconPin.cs`, and the version in `installer\THIRD-PARTY-NOTICES.txt` and `installer\nefcon-LICENSE.txt` if the license changed.
4. Run `.\build.ps1 PrepareNefcon` and the test suite.
