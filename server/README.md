# Vestigo.Server

ASP.NET Core 10 host for the Vestigo MCP server: Streamable HTTP at `/mcp`, an anonymous landing page at `/`, anonymous `/health`, and optional `/files` transfer when the Files module is enabled. The product overview, installer, and security model are in the [repository README](../README.md).

Settings and spools are outside the content root; see [configuration](../docs/configuration.md).
