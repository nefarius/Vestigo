# Symbols

WPP message formatting needs a decoding context before events are decoded. Realtime capture cannot discover PDB identities from the live session, so pass PDB or TMF paths to `start_capture`, or point `binaryPaths` at the installed binary and let Vestigo resolve the matching PDB.

Resolution order, implemented by `Nefarius.Utilities.ETW.Symbols.SymbolStore`, is:

1. The local symbol cache, laid out as `<file>/<GUID><AGE>/<file>`.
2. PDB files under the effective input roots (the built-in `traces` folder when `AllowedInputRoots` is empty), or explicit paths, whose embedded GUID and age match.
3. An HTTP symbol server, when `SymbolServer` or a supported `_NT_SYMBOL_PATH` segment is configured.

Supported environment segments are `srv*cache*url`, `srv*url`, and `cache*dir`. An explicit `SymbolServer` or `SymbolCache` value bypasses `_NT_SYMBOL_PATH`.

TMF files supply message formats. They do not supply provider GUIDs. PDB files can supply both. Provider-name rewrite is enabled only when a loaded PDB has a usable WPP control name.

Explicit `SymbolServer` or `SymbolCache` values take precedence over `_NT_SYMBOL_PATH`. Effective input roots are searched only for PDB files whose embedded GUID and age match; they are not a general symbol path. Do not configure a whole volume as an input root: each unresolved PDB walks that tree recursively.

## Remote workflow

Call `get_trace_settings` first. On a default install the built-in folder is also the named root `traces` (Read, Write).

1. Prefer `resolve_binary_symbols` (or `start_capture` / `attach_etw_session` with `binaryPaths`) on the installed `.sys`, `.dll`, or `.exe`. The CodeView identity is looked up in the cache, then `traces`, then the symbol server.
2. If the PDB is private and not on any server, upload it with `get_upload_url traces:driver.pdb`, or upload a zip and call `extract_trace_archive`.
3. Offline, `scan_etl_metadata` reports the PDB name, GUID, age, and symbol-server index prefix. `convert_etl` with `resolveSymbols: true` uses the same lookup.

## Symbol server authentication

`Vestigo:SymbolServerAuth` is admin-only configuration. Agents never see the secret. `get_trace_settings` reports only `{ scheme, configured }`.

Azure DevOps Artifacts (Basic with a personal access token):

```json
{
  "Vestigo": {
    "SymbolServer": "https://pkgs.dev.azure.com/<org>/_apis/symbol/symsrv",
    "SymbolServerAuth": {
      "Scheme": "Basic",
      "Username": "anything",
      "Secret": "<pat>"
    }
  }
}
```

WinDbgSymbolsCachingProxy (`X-Api-Key`):

```json
{
  "Vestigo": {
    "SymbolServer": "https://symbols.example/download/symbols",
    "SymbolServerAuth": {
      "Scheme": "Header",
      "HeaderName": "X-Api-Key",
      "Secret": "<key>"
    }
  }
}
```

A `user:pass@` prefix on `SymbolServer` is converted to Basic and stripped from the URL. Credentials are sent only to that origin. Redirects that leave the origin (for example Azure Blob SAS URLs) do not receive the header. HTTPS is required except on loopback. A 401 or 403 is returned as `symbol_auth_failed`.

Practical public-server setup:

```text
_NT_SYMBOL_PATH=srv*C:\symbols*https://msdl.microsoft.com/download/symbols
```

Or set the same pair in the machine settings and ignore the environment:

```json
{
  "Vestigo": {
    "SymbolCache": "C:\\symbols",
    "SymbolServer": "https://msdl.microsoft.com/download/symbols"
  }
}
```

Downloaded files stay in the cache directory. Vestigo does not delete them as part of spool retention. Files you copy into `traces` are user data and are never deleted automatically. If a configured `SymbolCache` sits outside the settings tree, allow write access for the trusted identity that runs Vestigo (the configured MSI service account, or the user running `dotnet run`). Do not share a writable cache between a privileged service and an untrusted user; see [security.md](security.md) and [configuration.md](configuration.md). Use [troubleshooting.md](troubleshooting.md#decoded-text-is-missing) when formats are missing.
