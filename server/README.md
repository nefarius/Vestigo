# Vestigo.Server

ASP.NET Core 10 host for the Vestigo MCP server: Streamable HTTP at `/mcp`, an anonymous landing page at `/`, anonymous `/health`, and optional `/files` transfer when the Files module is enabled (API token or a one-time `?ticket=` minted by the file tools). The product overview, installer, and security model are in the [repository README](../README.md).

Settings and spools are outside the content root; see [configuration](../docs/configuration.md).

`/config` is the MudBlazor settings page for direct loopback connections. It edits every `Vestigo` setting and can restart the installed Windows service. It bypasses API-token authentication for local users, so never proxy it or its `/_blazor`, `/_framework` and `/_content` routes. See the [local administration security boundary](../docs/security.md#local-configuration).

API 1.1 plugin tool providers share `/mcp` with built-in tools. An authenticated client
can select an application session using `X-Vestigo-Session-Id` or request metadata
`vestigo/sessionId`; the HTTP transport remains stateless. See the
[provider and identity contract](../docs/plugins.md#api-11-tool-providers).
