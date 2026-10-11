# Security

Vestigo binds to a loopback address and runs as a Windows service so realtime ETW session creation can be elevated. That privilege is local administrator or `SeSystemProfilePrivilege`.

When `ApiTokens` is empty, `/mcp` accepts anonymous clients. That is the default for a fresh localhost install. Add one or more tokens to require `Authorization: Bearer <token>`. Comparison uses SHA-256 digests and a fixed-time check. Tokens are not logged and are not copied into claims.

`/health` stays anonymous so a local supervisor can probe the process without a token. A missing or invalid token receives HTTP 401 and `WWW-Authenticate: Bearer`.

Every client path is canonicalized and must stay inside the configured input or output root. A root of `D:\traces` does not allow `D:\traces-extra`. Unsupported extensions are rejected. Existing outputs are kept unless `overwrite` is true. Input size, event count, output size, and capture lifetime are capped.

Spools, logs, the symbol cache, and owned-session state are stored under ProgramData, outside the install directory. Restrict that directory to the service account and local administrators.
