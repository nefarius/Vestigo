# Troubleshooting

## `/mcp` returns 401

`ApiTokens` is configured and the request did not include a matching bearer token. `/health` remains anonymous. The log records the authentication failure without the token value.

## Startup fails immediately

The exception names the setting. Common causes:

- `ListenAddress` is not an IP address, `Kestrel:Endpoints` is set, or `ListenPort` is outside 1-65535.
- A token, attachable-session, root name, or protected-name entry is blank.
- A size, count, or duration limit is not positive, or `MaxReadWait` is outside 0-2 minutes.
- A named root is missing an absolute path, has a duplicate name, has no access flags, or uses a generated volume-root name (`drive-c`, …) while `VolumeRoots` is not `None`.
- `VolumeRoots` is set and `VolumeRootAccess` has no flags.
- `Vestigo:Modules:<Name>` names an unknown module, or an enabled module depends on a disabled one (`Processes` and `Software` need `Jobs`; `RemoteDebugger` needs `Jobs` and `Processes`).
- `Devices:NefconPath`, `RemoteDebugger:ExtraSearchPaths`, or another module-specific setting failed validation.

`Refusing to use the Vestigo state directory: ...` means the state tree under `%ProgramData%\Nefarius Software Solutions\Vestigo` failed verification: a path component is a link or junction, a directory has an untrusted owner, or a parent can be modified by non-administrators. Remove the link, or take ownership as Administrators and fix the permissions, or reinstall the MSI (it re-applies the protected ACL), then restart the service.

A startup `SECURITY WARNING` is not a failure. A non-loopback bind without `ApiTokens` means anyone who can reach the port on the network can use `/mcp`. A volume-root warning means `VolumeRoots` granted Write or Execute on whole volumes. A privileged-module warning can also appear on loopback: without `ApiTokens`, any local client can use enabled `Processes`, `Devices`, or `RemoteDebugger`. `Services` is included only when the server binds beyond loopback, `Software:AllowUninstall` only when the Software module is also enabled, `System:AllowBootConfigurationChanges` only when the System module is also enabled, `Desktop` when that module is enabled (window titles from the console user's desktop; screenshots only when `Desktop:AllowScreenshots` is also on), `Audio` when that module is enabled (audio devices and the processes using them; changing defaults only when `Audio:AllowSetDefault` is also on), and `DebugOutput` when that module is enabled (it can read `OutputDebugString` from every session).

## The server is not reachable from another machine

`ListenAddress` must be `0.0.0.0` or `::` (or the machine address), and the inbound firewall rule `Vestigo MCP Server` must exist for the port. The MSI adds it for the "any address" modes. Restart the service after editing `appsettings.json`. A startup `SECURITY WARNING` in the log means no `ApiTokens` are set while the server is reachable from the network.

## Paths are rejected

Trace tools use `AllowedInputRoots` and `AllowedOutputRoots`. `outside_root` means the canonical path is outside those lists. `roots_not_configured` means the list needed for that operation is empty. `unsupported_extension` means the file is not `.etl`, `.pdb`, `.tmf`, `.json`, or `.ndjson` for the operation that received it. `already_exists` means the output is present and `overwrite` is false.

File, log, process, and device tools use named `Vestigo:Roots` plus any `VolumeRoots`. `unknown_root` means the `name:` prefix is not configured. `access_denied` means that root lacks the `Read`, `Write`, or `Execute` flag the operation needs, or Windows denied the file. `roots_not_configured` means both `Vestigo:Roots` and `VolumeRoots` produced no roots. Per-user mapped network drives are not visible to the LocalSystem service.

## Realtime capture fails

`elevation_required` means the process could not create an ETW session. Run the service elevated. `session_exists` means that session name is already active; restart Vestigo so it can stop its own orphan, or stop the named session deliberately. `providers_required` means the call had neither provider GUIDs nor a PDB with WPP control GUIDs.

## Attaching to a session fails

`attach_not_allowed` means the name is not matched by `Vestigo:AttachableSessions`. `session_not_found` means no running session has that name (see `list_etw_sessions`). `invalid_request` for a `Vestigo-` name means that session is owned by a capture; use `read_capture` on it. `attach_failed`, or a capture that ends `Failed`, usually means the session is not a realtime session or the process lacks permission to consume it.

## Health is degraded

The process is up, but `AllowedInputRoots` or `AllowedOutputRoots` is still empty. Only the Trace tools that take ETL, PDB, TMF, or conversion-output paths fail until those lists are set. Named `Roots` and `VolumeRoots` tools keep working. A default MSI install reports Degraded for this reason. An unhealthy result means the spool directory is not writable.

## Interactive-session tools fail

`close_app`, `list_windows`, `list_displays`, `list_audio_endpoints`, and `set_default_audio_endpoint` default to `context: interactive` (the console user's session through the session helper). `context: service` runs those tools in-process on the service desktop and does not need a console user. `take_screenshot` is interactive only. `run_process` defaults to `service`; only `context: interactive` needs a console user.

`no_interactive_session` means nobody is logged on at the console. `token_unavailable` means the console-user token could not be opened (the service needs SYSTEM). `elevation_unavailable` means `elevated: true` was requested and the console user has no linked administrator token. `desktop_unavailable` means the lock screen or UAC secure desktop, no displays are attached, `GetDC` failed, or the desktop blit failed. A failed `PrintWindow` is not enough: capture falls back to `BitBlt`. `helper_timeout` means the session helper did not finish in time. `helper_failed` means it exited without a usable JSON result, or a screenshot PNG could not be decoded.

`screenshots_disabled` means `Vestigo:Desktop:AllowScreenshots` is false. `set_default_disabled` means `Vestigo:Audio:AllowSetDefault` is false. `boot_changes_disabled` means `Vestigo:System:AllowBootConfigurationChanges` is false. `secure_boot_enabled` means `set_test_signing` tried to turn test signing on while Secure Boot is on or its state is unknown.

## The updater does not offer a release

The installed configuration must remain at `%ProgramFiles%\Nefarius Software Solutions\Vestigo\Nefarius_Vestigo_Updater.json`. Missing updater files or the version registry value are expected when the `Updater` feature was deselected; use Modify to add Automatic updates, or reinstall without `REMOVE=Updater`. The registry value `HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version` must be the installed MSI product version. A manifest whose `manifestVersion` is older than the last one this user accepted is rejected. Exit code 104 means the manifest or its `.minisig` sidecar could not be verified. Exit code 116 means the downloaded MSI checksum or Authenticode publisher check failed.

## Debug-output capture fails

`executable_missing` means `tools\DebugViewConsole.exe` is not next to Vestigo.exe and `Vestigo:DebugOutput:ExecutablePath` is unset. Reinstall or stage the pinned asset with `.\build.ps1 PrepareDebugView`. `hash_mismatch` means the file's SHA-256 is not `Vestigo:DebugOutput:ExpectedSha256`; set that value to empty only for a path you replaced on purpose. `capture_busy` means another debug-output capture is still running; stop it first. `dbwin_in_use` means another DebugView, DebugView++, or similar reader already owns `DBWIN_BUFFER`. Close that viewer and retry. Vestigo never sends DebugViewConsole's `-x` flag, because that would stop every instance on the machine.

Messages from a process that has a debugger attached go to the debugger, not to this capture. Kernel `DbgPrint` is not captured. In session 0 the local and Global buffers are the same mapping.

## Decoded text is missing

`MissingFormatCount` counts WPP events whose message format was not in the loaded PDB or TMF sources. Add the matching symbol file or enable symbol resolution for offline ETL. Enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` stay numeric.
