# MCP tools

Generated from `McpServerTool` attributes. Do not edit by hand.

## `cancel_job`

Requests cancellation of a running job. Process jobs kill their process tree.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |

## `close_app`

Politely closes applications by posting WM_CLOSE to their top-level windows. Select by pid, processName and/or titleRegex (filters combine: pid or name, narrowed by title). Waits gracefulTimeoutSeconds, then reports which processes are still alive; it never kills them. context 'interactive' (default) acts on the console user's desktop.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id. |
| `processName` | `string` | Process name, with or without .exe. |
| `titleRegex` | `string` | .NET regular expression matched against window titles (case-insensitive). |
| `gracefulTimeoutSeconds` | `int` | Seconds to wait for exit after WM_CLOSE, 0 to 600. |
| `context` | `string` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | Interactive only: use the elevated token so elevated windows can be closed. |

## `convert_etl`

Decodes ETL files to a JSON or NDJSON file inside an allowed output root and returns a summary.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |
| `outputPath` | `string` | Output .json or .ndjson path inside an allowed output root. |
| `format` | `string` | json or ndjson. |
| `symbolPaths` | `string[]` | Optional explicit PDB and TMF paths. |
| `resolveSymbols` | `bool` | Resolve PDB references through the configured symbol cache and server. |
| `overwrite` | `bool` | Replace an existing output file. |

## `get_capture`

Returns status, counters, and the last error for one capture.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `get_job`

Returns state, exit code, and output size for one job.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |

## `get_remote_debugger_status`

Reports, for each Visual Studio version (2022, 2026) and architecture (x64, arm64, x86), whether msvsmon.exe is installed (path, file version, how it was found), plus running msvsmon processes with pid, session id, port (from the command line when readable) and whether that port is listening.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | Optional filter: '2022' or '2026'. |
| `arch` | `string` | Optional filter: 'x64', 'arm64' or 'x86'. |

## `inspect_wpp_pdb`

Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPath` | `string` | Absolute or rooted path to a .pdb file. |

## `install_remote_debugger`

Installs the Visual Studio Remote Debugging Tools as a job: downloads the official installer (aka.ms) into the spool, requires a valid Microsoft Corporation Authenticode signature, then runs it silently (/install /quiet /norestart) as the service account. Returns the job id; poll with wait_job or read_job_output. Skipped when msvsmon is already installed unless force is true.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `force` | `bool` | Reinstall even when msvsmon.exe is already present. |

## `kill_process`

Terminates a process by pid or exact name. entireTree also kills its descendants. Refuses the Vestigo process, pid 0 and pid 4.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id. |
| `processName` | `string` | Exact process name, with or without .exe. Every match is killed. |
| `entireTree` | `bool` | Also kill child processes. |

## `list_captures`

Lists captures owned by this Vestigo process.

No parameters.

## `list_etw_sessions`

Lists ETW session names currently running on this machine.

No parameters.

## `list_jobs`

Lists background jobs owned by this Vestigo process, newest first.

No parameters.

## `list_processes`

Lists running processes with pid, name, session id and start time. Filters are optional.

| Parameter | Type | Description |
| --- | --- | --- |
| `nameFilter` | `string` | Case-insensitive substring of the process name. |
| `pid` | `int?` | Only this process id. |
| `sessionId` | `int?` | Only this session id (0 is services, the console session is usually 1). |
| `limit` | `int` | Maximum rows, 1 to 2000. |

## `prepare_remote_debugger_computer`

Runs 'msvsmon /prepcomputer /private /quiet' as a job: configures the Windows firewall and permissions for remote debugging on private networks. It changes firewall rules; needs the service context (default). Returns the job id.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `context` | `string` | 'service' (default) or 'interactive'. |

## `read_capture`

Reads a page of decoded NDJSON events from a capture spool.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |
| `afterSequence` | `long` | Return events after this sequence. Use 0 to start at the beginning. |
| `limit` | `int?` | Maximum events to return. The server clamps this to its configured batch size. |

## `read_job_output`

Reads a page of text output from a job. Pass the returned NextOffset to continue.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |
| `offset` | `long` | Byte offset to read from. Use 0 for the beginning. |
| `limit` | `int?` | Maximum bytes to return. Clamped to MaxJobReadBytes. |

## `run_process`

Starts a process as a job. path is a file under a root with Execute access (name:relative\path or absolute), or a bare name such as cmd.exe when AllowPathLookup is enabled. .ps1 files run through pwsh.exe, else powershell.exe. context 'service' runs as the Vestigo service account; 'interactive' runs as the console user on winsta0\default (elevated: true uses the linked administrator token). stdout and stderr go to the job output. wait > 0 waits that many seconds and returns the output tail; wait 0 returns the job id immediately.

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | Executable or script path, or a bare name resolved from PATH. |
| `args` | `string[]` | Command-line arguments, one array element per argument. |
| `workingDirectory` | `string` | Working directory under a root with Read access. Defaults to the executable's directory. |
| `env` | `Dictionary`2` | Environment variable overrides. |
| `context` | `string` | 'service' (default) or 'interactive'. |
| `elevated` | `bool` | Interactive only: run with the elevated linked token. |
| `wait` | `int` | Seconds to wait (0 to 300) before returning. 0 returns the job id immediately. |

## `scan_etl_metadata`

Lists distinct PDB identities referenced by one or more ETL files.

| Parameter | Type | Description |
| --- | --- | --- |
| `etlPaths` | `string[]` | ETL paths inside an allowed input root. |

## `start_capture`

Starts a managed realtime WPP capture. Events are spooled locally; use read_capture to page them.

| Parameter | Type | Description |
| --- | --- | --- |
| `pdbPaths` | `string[]` | PDB paths whose WPP control GUIDs should be enabled. |
| `tmfPaths` | `string[]` | TMF paths used only for message decoding. |
| `providerIds` | `string[]` | Additional ETW provider GUIDs. |
| `level` | `string` | Trace level: None, Critical, Error, Warning, Information, or Verbose. |
| `matchAnyKeyword` | `string` | Match-any keyword bitmask. Omit for all keywords. |
| `matchAllKeyword` | `string` | Match-all keyword bitmask. Omit for no match-all filter. |
| `duration` | `string` | Maximum capture duration, for example 00:05:00. Capped by server configuration. |

## `start_remote_debugger`

Starts msvsmon.exe with /nostatus /silent /nofirewallwarn, /port, /timeout and the chosen authentication. Default context is 'interactive' (the console user's desktop), so attach rights match that user. auth 'windows' (default) authenticates callers; allowUsers adds /allow entries. auth 'none' disables authentication completely and requires iUnderstandNoAuthIsInsecure: true. Refuses a second instance on the same port. Connect Visual Studio to <host>:<port> afterwards.

| Parameter | Type | Description |
| --- | --- | --- |
| `version` | `string` | '2022' or '2026'. |
| `arch` | `string` | 'x64' (default), 'arm64' or 'x86'. |
| `port` | `int?` | TCP port; defaults to Vestigo:RemoteDebugger:DefaultPort (4026). |
| `timeoutSeconds` | `int` | Seconds msvsmon waits without a connection before exiting, 30 to 86400. |
| `auth` | `string` | 'windows' (default) or 'none'. |
| `iUnderstandNoAuthIsInsecure` | `bool` | Required to be true when auth is 'none'. Acknowledges that anyone reaching the port can run code. |
| `allowUsers` | `string[]` | Windows users allowed to connect (DOMAIN\user or user@domain), each passed as /allow. Only with auth 'windows'. |
| `context` | `string` | 'interactive' (default) or 'service'. |
| `elevated` | `bool` | Interactive only: run with the elevated linked token so elevated processes can be debugged. |

## `stop_capture`

Stops a capture and its ETW session.

| Parameter | Type | Description |
| --- | --- | --- |
| `captureId` | `string` | Capture identifier. |

## `stop_remote_debugger`

Stops msvsmon.exe by pid, or every instance started by Vestigo (all: true). Tries a graceful close, waits gracefulTimeoutSeconds, then kills the process. Only msvsmon.exe processes are accepted.

| Parameter | Type | Description |
| --- | --- | --- |
| `pid` | `int?` | Process id of an msvsmon.exe. |
| `all` | `bool` | Stop all instances started by Vestigo. |
| `gracefulTimeoutSeconds` | `int` | Seconds to wait after the graceful close before killing, 0 to 120. |

## `validate_decoding_sources`

Loads PDB and TMF files and reports whether they can decode WPP messages.

| Parameter | Type | Description |
| --- | --- | --- |
| `symbolPaths` | `string[]` | PDB and TMF paths inside an allowed input root. |

## `wait_job`

Waits up to the given number of seconds for a job to finish and returns its state.

| Parameter | Type | Description |
| --- | --- | --- |
| `jobId` | `string` | Job identifier. |
| `timeoutSeconds` | `int` | Maximum seconds to wait, 0 to 300. |

