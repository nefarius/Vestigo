# Troubleshooting

## `/mcp` returns 401

`ApiTokens` is configured and the request did not include a matching bearer token. `/health` remains anonymous. The log records the authentication failure without the token value.

## Startup fails immediately

The listen address is not loopback, a limit is not positive, or a token entry is blank. The exception names the setting.

## Paths are rejected

`outside_root` means the canonical path is outside the configured roots. `roots_not_configured` means that list is empty. `unsupported_extension` means the file is not `.etl`, `.pdb`, `.tmf`, `.json`, or `.ndjson` for the operation that received it. `already_exists` means the output is present and `overwrite` is false.

## Realtime capture fails

`elevation_required` means the process could not create an ETW session. Run the service elevated. `session_exists` means that session name is already active; restart Vestigo so it can stop its own orphan, or stop the named session deliberately. `providers_required` means the call had neither provider GUIDs nor a PDB with WPP control GUIDs.

## Health is degraded

The process is up, but an allowed input or output root is still empty. Tools that take paths fail until those roots are set. An unhealthy result means the spool directory is not writable.

## Decoded text is missing

`MissingFormatCount` counts WPP events whose message format was not in the loaded PDB or TMF sources. Add the matching symbol file or enable symbol resolution for offline ETL. Enum names for `%!ItemEnum!` and `%!ItemFlagsEnum!` stay numeric.
