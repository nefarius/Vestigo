# Release

Version numbers come from MinVer and `v*` tags. The MSI product version is read from the published `Vestigo.exe`.

Pull requests and `master` build an unsigned MSI on Windows and run the test suite. They do not upload the MSI.

A `v*` tag:

1. Publishes `win-x64` Release binaries once.
2. Checks that `Vestigo.exe` and `Vestigo.dll` are unsigned.
3. Signs both with SignRelay.
4. Builds `publish-x64\installer\Vestigo.msi` from those signed binaries.
5. Signs and verifies the MSI.
6. Uploads the MSI artifact and posts the Buildbot webhook.

The tag pipeline requires `SIGN_RELAY_SERVER`, `SIGN_RELAY_CI_TOKEN`, and `WEBHOOK_URL`.

WiX is pinned to CLI `5.0.2` and `WixToolset.UI.wixext/5.0.2`. The WixSharp project GUID is `7c2e1a44-6b8f-4d3a-9e15-2f6a8c0d4b71`. The MSI product code is generated for each build. The upgrade code is `9a4d6e28-1c7b-4f05-8d3e-5b2c7a9f0e64`. A later MSI with that upgrade code replaces an earlier Vestigo installation. The installer stops when the x64 .NET 10 ASP.NET Core runtime is missing.

Keep `installer/License.rtf` aligned with `LICENSE`.
