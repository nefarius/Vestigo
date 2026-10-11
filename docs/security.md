# Security

Vestigo binds to a loopback address and runs as a Windows service so realtime ETW session creation can be elevated. That privilege is local administrator or `SeSystemProfilePrivilege`.

When `ApiTokens` is empty, `/mcp` accepts anonymous clients. That is the default for a fresh localhost install. Add one or more tokens to require `Authorization: Bearer <token>`. Comparison uses SHA-256 digests and a fixed-time check. Tokens are not logged and are not copied into claims.

`/health` stays anonymous so a local supervisor can probe the process without a token. A missing or invalid token receives HTTP 401 and `WWW-Authenticate: Bearer`.

Every client path is canonicalized, including existing directory links, and must stay inside the resolved input or output root. A root of `D:\traces` does not allow `D:\traces-extra` or a link that resolves outside that root. The accepted path is the resolved location, so a later open does not follow a link that was swapped after the check. Unsupported extensions are rejected. Existing outputs are kept unless `overwrite` is true. Input size, event count, output size, and capture lifetime are capped.

`Kestrel:Endpoints` is rejected at startup. The process binds only `Vestigo:ListenAddress` and `Vestigo:ListenPort`. IPv6 loopback is formatted with brackets.

Spools, logs, the symbol cache, and owned-session state are stored under ProgramData, outside the install directory. Restrict that directory to the service account and local administrators.
