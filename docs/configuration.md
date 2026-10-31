# Configuration

Machine settings live in `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. On non-Windows development hosts the same relative path is under the user data directory. `VESTIGO_SETTINGS_DIR` overrides the directory for tests.

The installed binaries stay in Program Files. The installer does not ship `appsettings.Production.json`, so a machine file survives upgrades.

Property defaults are generated in [generated/configuration.md](generated/configuration.md).

| Setting | Default | Purpose |
| --- | --- | --- |
| `ListenAddress` | `127.0.0.1` | Loopback address, including `::1`. Other addresses and `Kestrel:Endpoints` are rejected at startup. |
| `ListenPort` | `5098` | HTTP port. Changing it requires a service restart. |
| `ApiTokens` | empty | Bearer tokens. Empty means anonymous `/mcp`. |
| `AllowedInputRoots` | empty | Roots for ETL, PDB, and TMF paths. |
| `AllowedOutputRoots` | empty | Roots for conversion output. |
| `Roots` | empty | Named roots (`Name`, absolute `Path`, `Access` flags `Read, Write, Execute`). Clients address them as `name:relative\path` or by an absolute path inside a root. |
| `Modules` | all enabled | `Vestigo:Modules:<Name>:Enabled` switches a feature module (`Trace`, `Jobs`, `Environment`, `Logs`, `EventLog`) on or off. Unknown names fail startup. |
| `MaxConcurrentJobs` | 8 | Running background job limit. |
| `MaxJobOutputBytes` | 67108864 | Output kept per job; later output is dropped with a marker. |
| `JobRetention` | `1.00:00:00` | How long finished jobs and their output are kept. |
| `MaxJobReadBytes` | 65536 | Maximum bytes returned by one `read_job_output` call. |
| `Environment:ProtectedNames` | `COMSPEC`, `SystemRoot`, `windir` | Variable names (case-insensitive) that `set_environment_variable` refuses to set or unset in any scope. |
| `Logs:MaxTailLines` | 2000 | Maximum lines returned by one `tail_text_file` or `read_text_file_since` call. |
| `Logs:MaxTailBytes` | 1048576 | Maximum bytes examined by one call; a longer single line is cut. |
| `EventLog:MaxEvents` | 500 | Maximum events returned by one `query_event_log` call. |
| `EventLog:MaxScannedEvents` | 20000 | Maximum events examined per query, which bounds message text filters. |
| `SymbolServer` | null | Optional HTTP symbol server. Explicit values bypass `_NT_SYMBOL_PATH`. |
| `SymbolCache` | null | Cache directory. The default is `symcache` under the settings directory. |
| `MaxInputBytes` | 4294967296 | Maximum size of one input file. |
| `MaxEvents` | 1000000 | Maximum decoded events per conversion or capture. |
| `MaxOutputBytes` | 536870912 | Maximum bytes written per conversion or capture. |
| `MaxCaptureDuration` | `01:00:00` | Upper bound for a realtime capture. |
| `SpoolRetention` | `1.00:00:00` | How long a stopped spool is kept. |
| `MaxConcurrentCaptures` | 4 | Running capture limit. |
| `MaxReadBatch` | 200 | Maximum events returned by one `read_capture` call. |
| `BufferSizeKb` | 64 | ETW buffer size. |
| `FlushTimerSeconds` | 1 | ETW flush interval. |
| `ClockResolution` | `QueryPerformanceCounter` | `QueryPerformanceCounter`, `SystemTime`, or `CpuCycle`. |

Serilog writes console output and daily files under the settings `logs` directory. The default level is Information.
