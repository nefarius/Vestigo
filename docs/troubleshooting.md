# Troubleshooting

## `/mcp` returns 401

`ApiTokens` is configured and the request did not include a matching bearer token. `/health` remains anonymous. The log records the authentication failure without the token value.

## Startup fails immediately

The listen address is not an IP address, a limit is not positive, or a token entry is blank. The exception names the setting.

## The server is not reachable from another machine

`ListenAddress` must be `0.0.0.0` or `::` (or the machine address), and the inbound firewall rule `Vestigo MCP Server` must exist for the port. The MSI adds it for the "any address" modes. Restart the service after editing `appsettings.json`. A startup `SECURITY WARNING` in the log means no `ApiTokens` are set while the server is reachable from the network.

## Paths are rejected

`outside_root` means the canonical path is outside the configured roots. `roots_not_configured` means that list is empty. `unsupported_extension` means the file is not `.etl`, `.pdb`, `.tmf`, `.json`, or `.ndjson` for the operation that received it. `already_exists` means the output is present and `overwrite` is false.

## Realtime capture fails

`elevation_required` means the process could not create an ETW session. Run the service elevated. `session_exists` means that session name is already active; restart Vestigo so it can stop its own orphan, or stop the named session deliberately. `providers_required` means the call had neither provider GUIDs nor a PDB with WPP control GUIDs.

## Health is degraded

The process is up, but an allowed input or output root is still empty. Tools that take paths fail until those roots are set. An unhealthy result means the spool directory is not writable.

## The updater does not offer a release

The installed configuration must remain at `%ProgramFiles%\Nefarius Software Solutions\Vestigo\Nefarius_Vestigo_Updater.json`. The registry value `HKLM\SOFTWARE\Nefarius Software Solutions\Vestigo\Version` must be the installed MSI product version. A manifest whose `manifestVersion` is older than the last one this user accepted is rejected. Exit code 104 means the manifest or its `.minisig` sidecar could not be verified. Exit code 116 means the downloaded MSI checksum or Authenticode publisher check failed.

## Decoded text is missing

`MissingFormatCount` counts WPP events whose message format was not in the loaded PDB or TMF sources. Add the matching symbol file or enable symbol resolution for offline ETL. Enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` stay numeric.
