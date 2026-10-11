# Symbols

WPP message formatting needs a decoding context before events are decoded. Realtime capture cannot discover PDB identities from the live session, so pass PDB or TMF paths to `start_capture`. Offline conversion can also resolve PDB references embedded in the ETL file.

Resolution order, implemented by `Nefarius.Utilities.ETW.Symbols.SymbolStore`, is:

1. The local symbol cache, laid out as `<file>/<GUID><AGE>/<file>`.
2. PDB files under `AllowedInputRoots`, or explicit paths, whose embedded GUID and age match.
3. An HTTP symbol server, when `SymbolServer` or a supported `_NT_SYMBOL_PATH` segment is configured.

Supported environment segments are `srv*cache*url`, `srv*url`, and `cache*dir`. An explicit `SymbolServer` or `SymbolCache` value bypasses `_NT_SYMBOL_PATH`.

TMF files supply message formats. They do not supply provider GUIDs. PDB files can supply both. Provider-name rewrite is enabled only when a loaded PDB has a usable WPP control name.

Downloaded files stay in the cache directory. Vestigo does not delete them as part of spool retention.
