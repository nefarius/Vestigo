# Security

Vestigo binds to one IP address (loopback `127.0.0.1` by default) and runs as a Windows service so realtime ETW session creation can be elevated. That privilege is local administrator or `SeSystemProfilePrivilege`.

When `ApiTokens` is empty, `/mcp` accepts anonymous clients. That is the default for a fresh localhost install. Add one or more tokens to require `Authorization: Bearer <token>`. Comparison uses SHA-256 digests and a fixed-time check. Tokens are not logged and are not copied into claims.

`/health` stays anonymous so a local supervisor can probe the process without a token. A missing or invalid token receives HTTP 401 and `WWW-Authenticate: Bearer`.

Every client path is canonicalized, including existing directory links, and must stay inside the resolved input or output root. A root of `D:\traces` does not allow `D:\traces-extra` or a link that resolves outside that root. The accepted path is the resolved location, so a later open does not follow a link that was swapped after the check. Unsupported extensions are rejected. Existing outputs are kept unless `overwrite` is true. Input size, event count, output size, and capture lifetime are capped.

`Kestrel:Endpoints` is rejected at startup. The process binds only `Vestigo:ListenAddress` and `Vestigo:ListenPort`. IPv6 addresses are formatted with brackets.

## Network exposure

`ListenAddress` accepts any IP address, including `0.0.0.0` and `::`. Vestigo does not require a token, because a localhost-only install without one is a supported setup. The risk grows when the address is not loopback: anyone who can reach the port can start captures and read files inside the allowed roots. For that reason:

- Configure `ApiTokens` (strongly recommended) whenever `ListenAddress` is not loopback. Use long random values, for example 32 random bytes encoded as base64url.
- At startup Vestigo logs a prominent `SECURITY WARNING` when the address is non-loopback and `ApiTokens` is empty. The warning never contains token values.
- Traffic is plain HTTP. Tokens and traces cross the network unencrypted, so use a trusted network or a TLS-terminating proxy.
- Open the Windows firewall only for the chosen port and, where possible, restrict the rule's remote addresses.

The MSI installer lets you pick one of four modes: localhost without a token, localhost with a token, any address without a token (the dialog shows a warning), or any address with a token (the default). The choice is written to `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`, readable only by SYSTEM and Administrators, so the token is stored there in clear text. Modes that bind any address add an inbound TCP firewall rule for the port; uninstalling removes it. See [windows-service.md](windows-service.md) for the silent-install properties.

`set_environment_variable` with machine scope is a machine-wide configuration change that affects every process started afterwards, so treat access to `/mcp` as administrative when the module is enabled; disable it with `Vestigo:Modules:Environment:Enabled=false`. `Vestigo:Environment:ProtectedNames` (default `COMSPEC`, `SystemRoot`, `windir`) lists names that cannot be set or unset in any scope. Variable values are never written to the Vestigo log, and `set_environment_variable` does not echo the value. User scope needs the SYSTEM account (`SeTcbPrivilege`) to obtain the console user token and fails with `user_not_available` when nobody is logged on. Process scope changes only the Vestigo service process and its children.

Event log channels such as `Security` are limited by Windows access control for the service account; `.evtx` files and text files for `tail_text_file` must lie inside a configured root with `Read` access. Log and event content is returned to the client as data and can contain sensitive information. Text files are opened with `FileShare.ReadWrite | FileShare.Delete` so they can be read while another process writes them.

Spools, logs, the symbol cache, and owned-session state are stored under ProgramData, outside the install directory. Restrict that directory to the service account and local administrators.

Updates are checked by the packaged Vicius 1.20.0 executable, not by the loopback MCP service. The shipped `Nefarius_Vestigo_Updater.json` is authoritative and points only at `https://buildbot.nefarius.at/builds/Vestigo/latest/updates.json`. Vicius verifies `updates.json.minisig` with its embedded Nefarius minisign public key before parsing the manifest. The signed manifest carries the MSI size, lowercase SHA-256, and publisher pin. Vicius then requires a valid Authenticode chain whose simple subject name is `Nefarius Software Solutions e.U.`

The configuration and updater live under Program Files, so a non-administrator cannot retarget the feed. The stock updater binary also contains a Nefarius API address used only when that local configuration is absent. Rotating the minisign key requires a newer official Vicius build with the new public key compiled in; renewing the Authenticode certificate does not, while its subject name stays the same. The minisign private key is a release secret and is not stored in the repository or in build artifacts.

Named roots (`Vestigo:Roots`) gate the file tools and the `/files` endpoints. A root needs `Write` access before anything can be uploaded, copied in, moved, deleted or replaced there, and `Read` for downloads and listings. Uploads are executable-capable data: a client with `Write` access to a root that holds drivers, services or scripts can change what runs on the machine, and a service running as administrator makes that a privilege boundary. Grant `Write` only to roots that need it, and use API tokens whenever the endpoint is reachable by more than one local user. Uploads are bounded by `Vestigo:Files:MaxUploadBytes`, written to a temporary file next to the target and committed only after the optional SHA-256 matches. The `/files` endpoints reject `:` in paths to block NTFS alternate data streams, and a root, or a directory containing a root, cannot be deleted or moved. `fetch_file` can read UNC paths with the service account's identity, so restrict who may call it. Scheduling reboot-time deletes or replacements needs administrator rights; locked-file replacement leaves a `.vestigo-old-*` file until it can be removed.

nefcon (the device and driver CLI used by `Vestigo.Devices`) is bundled, not downloaded at run time. The build pins nefcon 1.21.0 by archive URL and SHA-256 (`build/NefconPin.cs`), extracts only `x64\nefconc.exe`, and requires a valid Authenticode signature whose simple subject name is `Nefarius Software Solutions e.U.` before the MSI installs it under `tools\` in Program Files. The runner only executes that file (or an absolute `Vestigo:Devices:NefconPath` set by an administrator), passes arguments as a discrete list with no shell, rejects values that start with `-`, and never accepts free-form arguments. The runner does not confine file paths itself; code that exposes it must apply `RootPolicy` first.

## Processes

`run_process`, `close_app` and `kill_process` execute and terminate arbitrary programs. This is by design: a client that can call them can run any program under the `Execute` roots, or any program on `PATH` while `AllowPathLookup` is true, as the service account (normally SYSTEM) or as the console user. Treat an API token for this module as administrator-equivalent. The module is off by default. Enabling it with `Vestigo:Modules:Processes:Enabled = true` requires the `Jobs` module and at least one entry in `ApiTokens`; startup fails otherwise.

Controls that do apply: programs must be files inside a root that has the `Execute` flag, using the same link-following canonicalization as other paths; working directories need `Read`; bare names are limited to executables found on the absolute entries of the service `PATH` (never the current directory) and can be turned off with `AllowPathLookup = false`; scripts must live under an `Execute` root. Environment overrides and arguments are bounded and checked for NUL characters. `kill_process` refuses the Vestigo process, pid 0 and pid 4. `close_app` only posts `WM_CLOSE` and never kills.

The interactive context needs the service to run as SYSTEM (for `WTSQueryUserToken`). The process runs as the logged-on user on the visible desktop, and `elevated` uses that user's own linked administrator token; it never grants rights the user does not have. Job output files hold the process output and are readable through `read_job_output` until `JobRetention` expires, so do not run commands that print secrets. The session helper only accepts `close-window` arguments and does no network or file access.
