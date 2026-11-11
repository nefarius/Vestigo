# AI-assisted Vestigo plugin development

These instructions apply to Vestigo plugin code and its private dependency projects.
Plugin authors may copy this file to the root of an independent plugin repository.
Follow the task instructions and any additional rules of the repository being edited.

## Start with the supported API

- In a Vestigo checkout, read `docs/plugins.md`, the public contracts in
  `server/Vestigo.Abstractions`, and `examples/Vestigo.ExamplePlugin` before editing.
  For an independent plugin, consult the target host release's
  [plugin guide](https://github.com/nefarius/Vestigo/blob/master/docs/plugins.md)
  and the contracts package actually referenced by that project. Verify signatures
  against that version; do not infer APIs from the server's internal classes.
- Establish the plugin ID, module names, tool names, dependencies, settings,
  endpoints and security-relevant capabilities. Keep new examples harmless.
- Keep corporate source, binaries, credentials and private feeds in the private
  plugin repository. Never add them to Vestigo's public source or release payload.

## Implement a plugin, not a second host

- Target .NET 10 (the host's Windows TFM when Windows APIs require it), enable
  `EnableDynamicLoading`, and reference `Microsoft.AspNetCore.App` plus a compatible
  released MCP SDK. Independent plugins reference `Vestigo.Abstractions` as a
  NuGet package; the in-repository example may use its existing project reference.
  Do not reference `Vestigo.Server` or copy host implementations into the plugin.
- Use `Vestigo.Extensibility.IVestigoPlugin` with a public concrete entry type and
  public parameterless constructor. Return explicit `VestigoModule` instances
  through `Modules`; do not scan unrelated assemblies or modify `ModuleRegistry.All`.
- Derive modules from `Vestigo.Extensibility.VestigoModule`. Register services in
  `ConfigureServices(IServiceCollection, PluginContext)` and use constructor DI.
  Read module settings from `PluginContext.Settings`. Do not build a second service
  provider, mutate the host provider at runtime, or require `VestigoOptions`.
- Contribute attributed MCP tool classes through `ToolTypes` to the existing `/mcp`
  pipeline. Assign stable explicit tool names; check collisions across public and
  non-public attributed methods using the SDK's resolved names, not class names.
- For protocol-defined catalogs, require API 1.1 and register singleton
  `IVestigoToolProvider` services. Reserve a stable prefix; let the host compose
  handlers. Preserve schemas/results and use the full `PluginSessionIdentity`
  owner/session key for isolation. Do not replace SDK handlers or infer sessions
  from client names/IPs. Providers own backend leases, concurrency and cleanup.
- Map HTTP routes through `MapEndpoints(IEndpointRouteBuilder, PluginContext)`.
  Paths are relative to `/plugins/<id>`. Preserve host authorization; never call
  `AllowAnonymous()` on an endpoint or route group. All valid host tokens can use
  all enabled plugin tools; implement additional authorization when required.
- Keep loading and external module activation opt-in. Constructors may run during
  discovery even for disabled modules: avoid side effects there. Declare module
  dependencies and informational `Capabilities`; capability declarations do not
  constrain execution. Bound work and output, honor cancellation, confine client
  paths to the plugin's configured allowed roots, and never log secrets or tokens.

## Package and deploy deliberately

- Ship a strict `plugin.json` with `id`, `version`, `hostApiVersion`, `entryAssembly`
  and `entryType`. IDs are 1–64 lowercase letters/digits/hyphens, starting with a
  letter, without trailing whitespace. The version is numeric; the API requirement
  is major.minor (currently 1.1; existing API 1.0 plugins remain supported), independent of the Vestigo application version.
  Use an entry DLL filename directly in the plugin directory, never an absolute
  path or traversal. Check the target host's compatibility policy before upgrading.
- Publish the complete dependency graph: entry DLL, `.deps.json`, manifest, private
  managed dependencies and native RID assets. Do not trim or single-file bundle a
  plugin. Share the host's contracts/SDK identities; do not try to bypass a rejected
  contract version by loading another copy. Private dependencies stay plugin-local.
- Use a protected plugin root separate from the host installation. In development,
  use an isolated `VESTIGO_SETTINGS_DIR` and its reserved `plugins` subdirectory.
  Preserve ACL, reparse-point and device-path checks. Never make executable code
  writable through MCP file tools or relax trust checks to get a test passing.
- Plugins are trusted in-process code with the service's privileges, possibly
  LocalSystem. AssemblyLoadContext provides dependency isolation, not a security
  sandbox. Use an explicit administrative or pinned-package trust policy; loading
  and enabling modules are separate administrator choices. Updates require restart.
  Keep local verification isolated from a live service unless deployment is in scope.

## Verify and hand off

- Test actual published artifacts, including an independent build without a server
  reference, private dependency resolution, and the intended host architecture.
  Verify MCP discovery and invocation, injected services, disabled-module absence,
  and authenticated HTTP routes when provided. Include negative tests for duplicate
  names, bad manifests, missing dependencies, anonymous groups and trust rejection
  when the change affects those behaviors. Assert the intended failure diagnostic.
- In this checkout, use `tests/Vestigo.Server.Tests/PluginTests.cs` and the published
  fixtures as references. With PowerShell 7.4+, run the deployment smoke script:
  `tests/Test-ExternalPluginDeployment.ps1 -HostPublishDirectory <host-publish> -PluginPublishDirectory <example-publish>`.
  That script expects the example's echo module and routes; adapt equivalent checks
  in an independent plugin repository rather than renaming a real plugin to fit it.
- Document manifest fields, module settings/defaults, tools, data disclosure,
  dependencies, trust requirements, installation/update steps and compatibility.
  Host contributions must also follow the host's documentation rules. Report builds,
  tests, skips and environment limitations accurately; distinguish new failures
  from a verified baseline. Never claim an MSI upgrade was tested using only a test host.
