# Windows service

The MSI installs an x64, per-machine, framework-dependent service. It requires the x64 .NET 10 ASP.NET Core runtime and replaces an earlier Vestigo installation that uses the same upgrade code.

- Service name: `Vestigo`
- Display name: `Vestigo MCP Server`
- Start: automatic, including on install
- Stop and remove: uninstall

Binaries go to `%ProgramFiles%\Nefarius Software Solutions\Vestigo`. Settings and spools go to `%ProgramData%\Nefarius Software Solutions\Vestigo`. The MSI creates that directory and its `logs`, `spool` and `symcache` subdirectories with a protected ACL (inheritance disabled) that allows only SYSTEM and Administrators, and fails the install if any of them is a link or junction. Upgrades re-apply the ACL.

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

The access page also takes the port (default 5098). The authentication page, for token mode, takes the token. It is pre-filled with a random suggestion (32 random bytes, base64url) that you can replace; tokens must be at least 16 characters without spaces. A deferred, elevated action runs before the service starts and writes the installer-owned settings into `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`: `Vestigo:ListenAddress`, `ListenPort`, `ApiTokens`, `Vestigo:Modules:<Name>:Enabled` for every module, and `Vestigo:Software:AllowUninstall`. Only non-owned settings are retained. The file is UTF-8 without BOM and its ACL allows only SYSTEM and Administrators. It holds the token in clear text. If the existing file is not valid JSON it is kept as `appsettings.json.bak-<time>` and a new one is written.

On upgrade the pages are pre-filled from the existing file: a loopback address maps to localhost, any other address to "any address", and a non-empty `ApiTokens` to token mode. The existing token is kept unless you change it. The pages can only read the file when the installer runs elevated; otherwise they show the defaults, and an untouched suggested token never replaces an existing one. A specific non-loopback address already in the file (for example `192.168.1.5`) is kept in the "any address" modes.

Silent installs set the same choices as public MSI properties (`VESTIGO_API_TOKEN` is hidden, so it is not written to the MSI log, but the command line itself may be recorded by your shell or deployment tool):

| Property | Values | Default |
| --- | --- | --- |
| `VESTIGO_LISTEN_MODE` | `localhost` or `any` | existing setting, else `any` |
| `VESTIGO_AUTH_MODE` | `none` or `token` | existing setting, else `token` |
| `VESTIGO_PORT` | 1-65535 | existing setting, else `5098` |
| `VESTIGO_API_TOKEN` | token text | existing token, else a generated one |

```powershell
msiexec /i Vestigo-v1.2.3.4.msi /qn VESTIGO_LISTEN_MODE=localhost VESTIGO_AUTH_MODE=none VESTIGO_PORT=5098
msiexec /i Vestigo-v1.2.3.4.msi /qn VESTIGO_LISTEN_MODE=any VESTIGO_AUTH_MODE=token VESTIGO_API_TOKEN=<your-token>
```

With token mode and no token on the command line (and none stored), the installer generates one; read it from the settings file before pointing an MCP client at `/mcp`. Invalid values fail the install. The firewall rule is removed on uninstall and when a repair or upgrade switches to a localhost mode. Repair and modify rewrite the installer-owned settings (`ListenAddress`, `ListenPort`, `ApiTokens`, `Vestigo:Modules:<Name>:Enabled`, and `Vestigo:Software:AllowUninstall`); only non-owned settings are retained. Uninstall removes the service and firewall rule and leaves the ProgramData settings tree in place.

## Modules

The page after authentication is the standard feature tree. `Vestigo MCP Server` (binaries and service) is required; each module is a child feature whose id is the module name, nested by dependency: `Processes` and `Software` sit below `Jobs`, `RemoteDebugger` below `Processes`, and `SoftwareUninstall` (`Vestigo:Software:AllowUninstall`) below `Software`. A child cannot be installed without its parent, which matches the server's dependency checks.

The defaults equal the server defaults in every listen mode: all modules selected except `Devices`, `Processes`, `RemoteDebugger`, `Desktop`, `DebugOutput` and `SoftwareUninstall`. Nothing is blocked by the listen or token mode. The authentication page only shows a note when no token is chosen, because every module you enable is then open to whoever can reach the server. A lab VLAN with any address, no token, and `Processes` plus `SoftwareUninstall` is allowed; the service logs a `SECURITY WARNING` at startup.

The same deferred action that writes the listen settings also writes `Vestigo:Modules:<Name>:Enabled` for every module and `Vestigo:Software:AllowUninstall`, from the feature states. The installer therefore owns those keys; a repair or modify rewrites them. On upgrade the page starts from the modules in the existing settings file (when the installer runs elevated, otherwise from the defaults). Silent installs use the standard feature properties, or `VESTIGO_MOD_<NAME>` (`1` or `0`, for example `VESTIGO_MOD_PROCESSES=1`) to seed the default level:

```powershell
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=Server,Jobs,Processes,Software,SoftwareUninstall VESTIGO_LISTEN_MODE=any VESTIGO_AUTH_MODE=none
msiexec /i Vestigo-v1.2.3.4.msi /qn ADDLOCAL=ALL REMOVE=Devices,Processes,RemoteDebugger,Desktop,DebugOutput,SoftwareUninstall
```

With `ADDLOCAL` only the listed features (and their required parents) are installed, so list every module you want. `ADDLOCAL=ALL` selects every feature, including the default-off ones, so the second example removes them again.

Listen address and port are read at process start. Restart the service after changing them.

The MSI registers Vicius for the installing user. At logon, and once a day, that user's scheduled task checks Buildbot and shows an update window when a newer signed release exists. Approving it elevates and runs the MSI with `/passive /norestart`. The MSI stops and restarts the service, so an in-flight capture ends. Another Windows user does not receive the schedule until an administrator runs `Nefarius_Vestigo_Updater.exe --install --silent --override-success-code 0` in that user's session. The update window can disable later notifications; `Nefarius_Vestigo_Updater.exe --show-options` restores them. Uninstall removes the current user's autostart entry and scheduled task. A failure to register or remove Vicius does not fail the service installation.
