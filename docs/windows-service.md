# Windows service

The MSI installs an x64, per-machine, framework-dependent service. It requires the x64 .NET Runtime 10.0 and the x64 ASP.NET Core Runtime 10.0 (compatible 10.0.x patches; the highest Microsoft.NETCore.App patch must be at least the highest Microsoft.AspNetCore.App patch; the ASP.NET Core 10.0 Hosting Bundle also satisfies this) and replaces an earlier Vestigo installation that uses the same upgrade code. Windows 11 on ARM64 is supported through x64 emulation; a native ARM64 runtime does not satisfy the check, and the x64 runtimes live under `%ProgramFiles%\dotnet\x64`. The installer stops with a message when no matching pair is present. A later major (11 or newer) does not count. In a silent install the same message is written to the MSI log.

- Service name: `Vestigo`
- Display name: `Vestigo MCP Server`
- Description: `Windows MCP server for driver and application development.`
- Start: automatic, including on install
- Stop and remove: uninstall

Binaries go to `%ProgramFiles%\Nefarius Software Solutions\Vestigo`. Settings and spools go to `%ProgramData%\Nefarius Software Solutions\Vestigo`. The MSI creates that directory and its `logs`, `spool`, `symcache` and `traces` subdirectories with a protected ACL (inheritance disabled) that allows only SYSTEM and Administrators, and fails the install if any of them is a link or junction. Upgrades re-apply the ACL.

The service account needs:

- read access to the allowed input roots and symbol cache
- write access to the allowed output roots, spool, logs, and settings directory
- elevation for `start_capture`

`list_etw_sessions`, ETL scanning, and ETL conversion use Windows ETW but do not create a session. A session left behind by a crash is recorded in `owned-sessions.json`. The next startup stops those `Vestigo-` names and ignores every other session.

## Listen mode and API token

After the license page the installer asks where the server listens, then whether it requires an API token. The combinations and their effect on `Vestigo:ListenAddress`, `ApiTokens`, and the firewall:

| Mode | `ListenAddress` | `ApiTokens` | Firewall rule |
| --- | --- | --- | --- |
| 1. Localhost, no token | `127.0.0.1` | empty | none |
| 2. Localhost, with token | `127.0.0.1` | one token | none |
| 3. Any address, no token (shows a warning) | `0.0.0.0` | empty | inbound TCP, chosen port |
| 4. Any address, with token (default) | `0.0.0.0` | one token | inbound TCP, chosen port |

The access page also takes the port (default 5098). The authentication page, for token mode, takes the token. It is pre-filled with a random suggestion (32 random bytes, base64url) that you can replace; tokens must be at least 16 characters without spaces. After authentication, a file-system page chooses which volumes become named roots (`VolumeRoots`), and unless you pick custom paths a second page chooses the access granted to those volumes (`VolumeRootAccess`; default Read, Write, and Execute). Custom `Vestigo:Roots` already in the settings file are kept and merged. A deferred, elevated action runs before the service starts and writes the installer-owned settings into `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`: `Vestigo:ListenAddress`, `ListenPort`, `ApiTokens`, `VolumeRoots`, `VolumeRootAccess`, `Vestigo:Modules:<Name>:Enabled` for every module, `Vestigo:Software:AllowUninstall`, `Vestigo:ScheduledTasks:AllowChanges`, `Vestigo:Audio:AllowSetDefault`, `Vestigo:System:AllowBootConfigurationChanges`, `Vestigo:System:AllowUacChanges`, `Vestigo:Desktop:AllowScreenshots`, `Vestigo:Diagnostics:AllowCaptureProbes`, `Vestigo:Diagnostics:AllowNetworkProbes`, and `Vestigo:Updates:AllowSelfUpdate`. Only non-owned settings are retained. The file is UTF-8 without BOM and its ACL allows only SYSTEM and Administrators. It holds the token in clear text. If the existing file is not valid JSON it is kept as `appsettings.json.bak-<time>` and a new one is written.

On upgrade the pages are pre-filled from the existing file: a loopback address maps to localhost, any other address to "any address", and a non-empty `ApiTokens` to token mode. The existing token is kept unless you change it. An existing `VolumeRoots` value selects the matching file-system page; if that key is missing and `Roots` is already set, the custom-paths choice is selected so the upgrade does not widen access. The pages can only read the file when the installer runs elevated; otherwise they show the defaults, and an untouched suggested token never replaces an existing one. A specific non-loopback address already in the file (for example `192.168.1.5`) is kept in the "any address" modes.

Silent installs set the same choices as public MSI properties (`VESTIGO_API_TOKEN` is hidden, so it is not written to the MSI log, but the command line itself may be recorded by your shell or deployment tool):

| Property | Values | Default |
| --- | --- | --- |
| `VESTIGO_LISTEN_MODE` | `localhost` or `any` | existing setting, else `any` |
| `VESTIGO_AUTH_MODE` | `none` or `token` | existing setting, else `token` |
| `VESTIGO_PORT` | 1-65535 | existing setting, else `5098` |
| `VESTIGO_API_TOKEN` | token text | existing token, else a generated one |
| `VESTIGO_ROOTS_MODE` | `custom`, `system`, `fixed`, `all`, or `live` | existing `VolumeRoots`, else `custom` on upgrade when `VolumeRoots` is missing and `Roots` is already set, else `fixed` |
| `VESTIGO_ROOTS_ACCESS` | `r`, `rw`, or `rwx` | existing `VolumeRootAccess`, else `rwx` |

```powershell
msiexec /i Vestigo-v1.2.3.4.msi /qn VESTIGO_LISTEN_MODE=localhost VESTIGO_AUTH_MODE=none VESTIGO_PORT=5098
msiexec /i Vestigo-v1.2.3.4.msi /qn VESTIGO_LISTEN_MODE=any VESTIGO_AUTH_MODE=token VESTIGO_API_TOKEN=<your-token>
```

With token mode and no token on the command line (and none stored), the installer generates one; read it from the settings file before pointing an MCP client at `/mcp`. Invalid values fail the install. The firewall rule is removed on uninstall and when a repair or upgrade switches to a localhost mode. Repair and modify rewrite the installer-owned settings (`ListenAddress`, `ListenPort`, `ApiTokens`, `VolumeRoots`, `VolumeRootAccess`, `Vestigo:Modules:<Name>:Enabled`, `Vestigo:Software:AllowUninstall`, `Vestigo:ScheduledTasks:AllowChanges`, `Vestigo:Audio:AllowSetDefault`, `Vestigo:System:AllowBootConfigurationChanges`, `Vestigo:System:AllowUacChanges`, `Vestigo:Desktop:AllowScreenshots`, `Vestigo:Diagnostics:AllowCaptureProbes`, `Vestigo:Diagnostics:AllowNetworkProbes`, and `Vestigo:Updates:AllowSelfUpdate`); only non-owned settings are retained. Uninstall removes the service and firewall rule and leaves the ProgramData settings tree in place.

## Modules

The page after the volume-root choices is the standard feature tree. `Vestigo MCP Server` (binaries and service) is required. `Automatic updates` (`Updater`) is a default-selected child that installs the Vicius updater and registers it for the installing user; uncheck it to skip both. Each module is a child feature whose id is the module name, nested by dependency: `Processes` and `Software` sit below `Jobs`, `RemoteDebugger` below `Processes`, `SoftwareUninstall` (`Vestigo:Software:AllowUninstall`) below `Software`, `ScheduledTaskChanges` (`Vestigo:ScheduledTasks:AllowChanges`) below `ScheduledTasks`, `AudioSetDefault` (`Vestigo:Audio:AllowSetDefault`) below `Audio`, `BootConfigurationChanges` (`Vestigo:System:AllowBootConfigurationChanges`) and `UacChanges` (`Vestigo:System:AllowUacChanges`) below `System`, `DesktopScreenshots` (`Vestigo:Desktop:AllowScreenshots`) below `Desktop`, `DiagnosticsCaptureProbes` (`Vestigo:Diagnostics:AllowCaptureProbes`) below `Diagnostics`, `DiagnosticsNetworkProbes` (`Vestigo:Diagnostics:AllowNetworkProbes`) below `Diagnostics`, and `SelfUpdate` (`Vestigo:Updates:AllowSelfUpdate`) below `Updates`. A child cannot be installed without its parent, which matches the server's dependency checks.

The defaults equal the server defaults in every listen mode: all modules selected except `Devices`, `Processes`, `RemoteDebugger`, `Desktop`, `DesktopScreenshots`, `Audio`, `AudioSetDefault`, `DebugOutput`, `Diagnostics`, `DiagnosticsCaptureProbes`, `DiagnosticsNetworkProbes`, `SoftwareUninstall`, `ScheduledTaskChanges`, `BootConfigurationChanges`, `UacChanges` and `SelfUpdate`. Nothing is blocked by the listen or token mode. The authentication page only shows a note when no token is chosen, because every module you enable is then open to whoever can reach the server. A lab VLAN with any address, no token, and `Processes` plus `SoftwareUninstall` is allowed; the service logs a `SECURITY WARNING` at startup.

The same deferred action that writes the listen settings also writes `Vestigo:Modules:<Name>:Enabled` for every module, `Vestigo:Software:AllowUninstall`, `Vestigo:ScheduledTasks:AllowChanges`, `Vestigo:Audio:AllowSetDefault`, `Vestigo:System:AllowBootConfigurationChanges`, `Vestigo:System:AllowUacChanges`, `Vestigo:Desktop:AllowScreenshots`, `Vestigo:Diagnostics:AllowCaptureProbes`, `Vestigo:Diagnostics:AllowNetworkProbes`, and `Vestigo:Updates:AllowSelfUpdate`, from the feature states. The installer therefore owns those keys; a repair or modify rewrites them. On upgrade the page starts from the modules in the existing settings file (when the installer runs elevated, otherwise from the defaults). An unselected new Screenshots feature is not written as disabled, so a hand-set `Desktop:AllowScreenshots` survives an unelevated upgrade unless the operator selects or removes that feature. Silent installs use the standard feature properties. `VESTIGO_MOD_<NAME>` (`1` or `0`, for example `VESTIGO_MOD_PROCESSES=1`) only seeds the default feature level when `ADDLOCAL` and `REMOVE` are omitted. Those properties set `Preselected`, and Windows Installer then ignores the `VESTIGO_MOD_*` level conditions, including values loaded from an existing install. List every feature that must stay enabled:

```powershell
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=Server,Updater,Jobs,Processes,Software,SoftwareUninstall,Updates,SelfUpdate VESTIGO_LISTEN_MODE=any VESTIGO_AUTH_MODE=none
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=ALL REMOVE=Devices,Processes,RemoteDebugger,Desktop,DesktopScreenshots,Audio,AudioSetDefault,DebugOutput,Diagnostics,DiagnosticsCaptureProbes,DiagnosticsNetworkProbes,SoftwareUninstall,ScheduledTaskChanges,BootConfigurationChanges,UacChanges,SelfUpdate
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=Server,Trace,Files,Environment,System,Logs,EventLog,Services,ScheduledTasks,Jobs,Software,Updates REMOVE=Updater
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=Server,Trace,Files,Environment,System,Logs,EventLog,Services,ScheduledTasks,Jobs,Software,Updates,Devices REMOVE=Updater
```

With `ADDLOCAL` only the listed features (and their required parents) are installed, so list every module you want and include `Updater` if you want automatic updates. `ADDLOCAL=ALL` selects every feature, including the default-off ones, so the second example removes those modules again. The third example keeps the default modules and skips the updater. Add `Devices` (or any other module) to `ADDLOCAL` when you need it, as in the fourth; `VESTIGO_MOD_DEVICES=1` does not enable Devices once `Preselected` is set.

Listen address and port are read at process start. Restart the service after changing them.

When `Automatic updates` is selected (the default), the MSI installs `Nefarius_Vestigo_Updater.exe` and its configuration and registers Vicius for the installing user. At logon, and once a day, that user's scheduled task checks Buildbot and shows an update window when a newer signed release exists. Approving it elevates and runs the MSI with `/passive /norestart`. The MSI stops and restarts the service, so an in-flight capture ends. `start_self_update` (when `Vestigo:Updates:AllowSelfUpdate` is true) launches the same updater with `--silent-update`; the temporary copy then runs that MSI, so the service restarts and the MCP session drops. Registration is skipped when the installing user is SYSTEM (`UserSID` `S-1-5-18`), so a service-triggered upgrade does not create a SYSTEM autostart entry or daily task. Another Windows user does not receive the schedule until an administrator runs `Nefarius_Vestigo_Updater.exe --install --silent --override-success-code 0` in that user's session. The update window can disable later notifications; `Nefarius_Vestigo_Updater.exe --show-options` restores them. Deselecting the feature, or `REMOVE=Updater` with an explicit `ADDLOCAL` list, omits the updater files, the version registry value, and registration. Modify can add or remove the feature later; removing it deregisters the current user's autostart entry and scheduled task before the files are deleted. Uninstall does the same. A major upgrade that leaves the updater unselected unregisters the previous user's scheduled task before the old product is removed, and registers again after InstallFinalize only when the feature stays selected. A failure to register or remove Vicius does not fail the service installation.
