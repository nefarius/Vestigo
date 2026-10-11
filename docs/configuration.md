# Configuration

Machine settings live in `%ProgramData%\Nefarius Software Solutions\Vestigo\appsettings.json`. On non-Windows development hosts the same relative path is under the user data directory. `VESTIGO_SETTINGS_DIR` overrides the directory for tests.

The installed binaries stay in Program Files. The installer does not ship `appsettings.Production.json`, so a machine file survives upgrades.

Property defaults are generated in [generated/configuration.md](generated/configuration.md).

| Setting | Default | Purpose |
| --- | --- | --- |
| `ListenAddress` | `127.0.0.1` | Loopback address. Other addresses are rejected at startup. |
| `ListenPort` | `5098` | HTTP port. Changing it requires a service restart. |
| `ApiTokens` | empty | Bearer tokens. Empty means anonymous `/mcp`. |
| `AllowedInputRoots` | empty | Roots for ETL, PDB, and TMF paths. |
| `AllowedOutputRoots` | empty | Roots for conversion output. |
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
