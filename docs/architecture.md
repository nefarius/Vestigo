# Architecture

Vestigo is one ASP.NET Core process. It hosts the official MCP Streamable HTTP endpoint at `/mcp` in stateless mode and a Windows service named `Vestigo`.

```mermaid
flowchart LR
    client[MCP client] --> mcp["/mcp"]
    mcp --> tools[Trace tools]
    tools --> offline[Offline conversion]
    tools --> jobs[Capture manager]
    offline --> etw[Nefarius.Utilities.ETW]
    jobs --> session[EtwRealtimeSession]
    session --> spool[ProgramData spool]
    jobs --> spool
```

Offline conversion scans or decodes `.etl` files and writes a bounded JSON or NDJSON file. Realtime capture creates a `Vestigo-` session, enables WPP providers, and appends decoded events to a spool. Clients page that spool with `read_capture`; the MCP call itself does not stream for the life of the trace.

The decoding and symbol-server behavior comes from `Nefarius.Utilities.ETW`. Vestigo does not copy that implementation. PDB and TMF loading, `_NT_SYMBOL_PATH` parsing, cache lookup, GUID/age checks, and symbol-server download are package APIs in `Nefarius.Utilities.ETW.Symbols`.

On startup Vestigo stops only session names previously recorded in its own `owned-sessions.json` file and only when they start with `Vestigo-`. Shutdown stops every capture this process still owns.

Vestigo does not support the NT Kernel Logger and does not write a raw ETL recording. A UI is not part of this release; if one is added later it must use MudBlazor.

## Modules

Features are `VestigoModule` classes listed in `ModuleRegistry`. A module contributes services, MCP tool types, and optional HTTP endpoints, and is switched with `Vestigo:Modules:<Name>:Enabled`. Current modules: `Trace` (the WPP and ETW tools below), `Jobs` (background jobs with paged output, wait, and cancel), and `Processes` (run, list, close, and kill processes; opt-in, requires `Jobs` and API tokens).

File-system access for new modules goes through `RootPolicy`: named roots with `Read`, `Write` and `Execute` flags and the same link-following canonicalization as the trace path policy. Long operations run as jobs so an MCP call stays short.

### Processes

`ProcessTools` resolves paths through `RootPolicy` (`Execute` for programs, `Read` for working directories), plans the launch (`LaunchPlanner`: `.ps1` host selection, validation, `CommandLineBuilder` quoting) and starts a job whose work calls `IProcessLauncher`. `ProcessLauncher` has two paths. The service path uses `System.Diagnostics.Process` with redirected output appended to the job spool. The interactive path runs as the console user: `WTSGetActiveConsoleSessionId` and `WTSQueryUserToken` (plus `TokenLinkedToken` for `elevated`), `CreateEnvironmentBlock`, then `CreateProcessAsUser` on `winsta0\default` with only the spool file and NUL in a `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. The process starts suspended, joins a job object with `KILL_ON_JOB_CLOSE`, then resumes. The approach is ported from SignRelay's interactive launcher through CsWin32 (`server/NativeMethods.txt`).

Window closing needs the target desktop, so `close_app` runs `Vestigo.exe --session-helper close-window` through the interactive launcher. `Program.cs` handles that flag before the web host is built; the helper uses `EnumWindows`, `GetWindowThreadProcessId` and `PostMessage`, prints JSON, and exits.
