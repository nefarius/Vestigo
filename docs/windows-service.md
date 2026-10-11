# Windows service

The MSI installs an x64, per-machine, framework-dependent service. It requires the x64 .NET 10 ASP.NET Core runtime and replaces an earlier Vestigo installation that uses the same upgrade code.

- Service name: `Vestigo`
- Display name: `Vestigo MCP Server`
- Start: automatic, including on install
- Stop and remove: uninstall

Binaries go to `%ProgramFiles%\Nefarius Software Solutions\Vestigo`. Settings and spools go to `%ProgramData%\Nefarius Software Solutions\Vestigo`.

The service account needs:

- read access to the allowed input roots and symbol cache
- write access to the allowed output roots, spool, logs, and settings directory
- elevation for `start_capture`

`list_etw_sessions`, ETL scanning, and ETL conversion use Windows ETW but do not create a session. A session left behind by a crash is recorded in `owned-sessions.json`. The next startup stops those `Vestigo-` names and ignores every other session.

Listen address and port are read at process start. Restart the service after changing them.
