# MCP tools

The generated parameter catalog is [generated/mcp-tools.md](generated/mcp-tools.md). Examples below use the default loopback endpoint.

## Offline

`inspect_wpp_pdb` returns WPP control GUIDs, names, and flag bits from one PDB.

`validate_decoding_sources` loads explicit PDB and TMF paths and reports whether a decoding context was built.

`scan_etl_metadata` returns the distinct PDB name, GUID, age, and symbol-server index prefix referenced by the ETL files.

`convert_etl` writes `json` or `ndjson` under an allowed output root. The tool result is a summary: path, event count, bytes, missing WPP formats, truncation, and symbol diagnostics. It does not return the trace payload.

## Realtime

`list_etw_sessions` returns every session name Windows reports. It does not stop sessions.

`start_capture` accepts provider GUIDs and/or PDB paths. PDB control GUIDs are enabled automatically. TMF files contribute message formats but not provider GUIDs. The call returns when the session has started, not when the trace ends. Keyword bitmasks accept a decimal value or a `0x` hexadecimal value. The level must be a named `TraceLevelSelection` value.

`list_captures` and `get_capture` report state, event count, bytes, missing formats, and the stop reason.

`read_capture` returns NDJSON lines after `afterSequence`. Pass the returned `NextSequence` to the next call. `limit` is clamped to `MaxReadBatch`.

`stop_capture` cancels the reader and disposes the ETW session.

Capture duration cannot exceed `MaxCaptureDuration`. The spool is removed after `SpoolRetention` once the capture has stopped.

## Jobs

`list_jobs`, `get_job`, `wait_job`, `read_job_output` and `cancel_job` manage background jobs that other tools start. `read_job_output` returns text after a byte `offset`; pass the returned `NextOffset` to continue. Finished jobs are kept for `JobRetention`.

## Files

Paths are `root:relative\path` (or an absolute path inside a root). Reads need `Read`, everything that changes files needs `Write`. A root, or a directory containing a root, can never be deleted or moved, and NTFS stream syntax (`file:stream`) is rejected.

`list_roots` returns each root with its access flags and free space. `list_directory` lists a directory (`maxEntries` up to 5000, `Truncated` tells you more exist). `stat_path` returns size and times, an optional SHA-256, and for `.exe`, `.dll` and `.sys` files the version resource plus the embedded Authenticode status from `WinVerifyTrust` (`Signed`, `Unsigned` or `Invalid`). Catalog-signed files, such as many Windows binaries, have no embedded signature and report `Unsigned`.

`copy_path`, `move_path`, `delete_path` (non-empty directories need `recursive`) and `make_directory` do what they say. Directory copies skip links.

`fetch_file` downloads an HTTP(S) URL or copies an ordinary UNC file (not `\\?\` or `\\.\` device paths) into a root as a background job; use `wait_job` and `read_job_output`. Pass `expectedSha256` to reject a wrong file, and `overwrite` to replace an existing one. The size is capped by `Vestigo:Files:MaxUploadBytes`.

`get_upload_url` returns the `PUT` URL for a root-relative path plus ready-to-run `curl.exe` and `Invoke-WebRequest` commands. `delete_file_on_reboot` schedules a deletion with `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`; the service must run elevated.

### HTTP transfer endpoints

Both endpoints use the same bearer authentication as `/mcp`.

`PUT /files/{root}/{path}?overwrite=false&deferToReboot=false` streams the request body to a temporary file in the target directory and commits it with an atomic replace. An optional `X-Content-SHA256` header is checked before the commit; a mismatch returns 422 and writes nothing. The response is JSON:

```json
{ "fullPath": "D:\\drops\\bin\\x.sys", "size": 11, "sha256": "...", "replacedExisting": false, "rebootRequired": false, "asideFile": null, "asideCleanup": null }
```

If the target is locked, for example a loaded driver or running executable, the old file is renamed to `<name>.vestigo-old-<guid>`, the new file takes its place, and the old one is deleted or scheduled for deletion at reboot (`asideCleanup`). With `deferToReboot=true` the new file instead becomes a pending replace through `MoveFileEx(DELAY_UNTIL_REBOOT | REPLACE_EXISTING)` and `rebootRequired` is `true`. If the file cannot be renamed either, the call fails with 423 and suggests `deferToReboot`. Status codes: 400 invalid path, 403 root lacks `Write`, 404 unknown root, 409 exists without `overwrite`, 413 over `MaxUploadBytes`, 422 hash mismatch.

`GET /files/{root}/{path}` streams a file from a root with `Read` access and supports `Range` requests.