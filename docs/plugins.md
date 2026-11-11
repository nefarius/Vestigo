# External plugins

Vestigo can host independently compiled public or proprietary .NET 10 plugins. Loading is optional and disabled by default. No plugin framework, second MCP transport, remote installation tool, hot reload or unloading is involved. Install, remove and update plugins while the service is stopped, then restart it.

## Contracts and architecture

`server/Vestigo.Abstractions` is a separately packable .NET 10 library. Its `Vestigo.Extensibility` namespace exposes `IVestigoPlugin`, `VestigoModule`, `PluginContext` and `PluginApi`. The entry point is a public concrete `IVestigoPlugin` with a public parameterless constructor; its `Modules` property explicitly returns one or more module instances. Manifest metadata is authoritative, so IDs and versions are not duplicated on the entry point. Only the named type in the named assembly is discovered; dependency DLLs are never scanned for plugins.

The public module contract contains `Name`, `Description`, `ToolTypes`, `Dependencies`, informational `Capabilities`, and service/endpoint hooks. The context supplies the module's `Vestigo:Modules:<Name>` configuration section, plugin content directory and authentication state. It exposes neither the server's options model nor a service locator. Use constructor injection for services. HTTP registration receives `IEndpointRouteBuilder` under `/plugins/<id>` rather than the full `WebApplication`.

Built-in `Vestigo.Modules.VestigoModule` derives from this contract and retains its existing options-based hooks and defaults. An adapter bridges external modules into the explicit built-in registry. All manifests and deployments are checked before entry-point construction; module dependencies and tool names are validated before service registration. Services are registered before the host builds its one provider. Enabled module constructors are validated by the provider, MCP tools join the existing stateless `/mcp` endpoint, and HTTP routes are mapped before serving requests.

Dependencies can name built-in modules, sibling modules or another plugin's modules. Names are case insensitive. Missing/disabled dependencies, duplicate module names and cycles abort startup. A deterministic topological order registers dependencies first. Independent modules retain built-in registry order followed by ordinal plugin-directory order.

## API compatibility and assembly identity

The plugin API is **1.1**, independent of the application version. `hostApiVersion` must have exactly two components. The major must match the host; the requested minor must be no newer than the host. API 1.0 and 1.1 are accepted; 0.x, 2.x, 1.2 and missing metadata are rejected. API 1.0 plugins remain binary-compatible; the contracts assembly identity stays 1.0.0.0 while the package version is 1.1.0. New tool providers require hostApiVersion 1.1. Minor releases must preserve existing signatures and behavior and may add optional APIs; breaking changes require a new major. A plugin can use the same API across multiple Vestigo releases.

Each plugin uses a noncollectible `AssemblyLoadContext` and `AssemblyDependencyResolver`. `Vestigo.Abstractions`, `ModelContextProtocol`, `ModelContextProtocol.Core`, `ModelContextProtocol.AspNetCore` and `Microsoft.Extensions.AI.Abstractions` share the host's default context. Runtime and ASP.NET Core framework assemblies are shared using an allowlist derived from their actual runtime directories. Shared names, culture, public key tokens and versions are checked. Explicit shared plugin/SDK contracts require matching major versions, and the host version must be at least the requested version. Framework assemblies may satisfy older major-version references when the host version is not older, including references from .NET Standard dependencies. Assembly references are checked recursively before instantiation. Other managed dependencies must resolve inside that plugin's published directory; they never silently fall back to arbitrary host packages. Different private versions can coexist.

Private native assets must resolve through the published dependency graph inside the deployment. Otherwise only System32 imports are allowed on Windows; cwd and PATH are not native fallbacks. Follow the native dependency's supported RID and transitive DLL loading requirements. Native libraries may still have process-wide state and OS loader limitations; assembly isolation cannot eliminate these constraints. Publish for the host architecture (the MSI host is win-x64). Single-file and trimmed plugin deployments are unsupported; keep the entry DLL and its `.deps.json` on disk.

## Trust and protected installation

**Plugins are trusted executable code in the Vestigo process. AssemblyLoadContext isolates binary dependencies, not security privileges.** A plugin can run as LocalSystem, change services, read secrets or bypass application policies. Capability declarations only inform unauthenticated startup warnings; they do not constrain code. Never install an untrusted plugin.

`Vestigo:Plugins` supports:

- `Enabled`: defaults to `false`; enabling it requires an explicit trust mode.
- `Root`: optional absolute directory outside the mutable settings tree (except the reserved default `plugins` subdirectory); defaults to `%ProgramData%\Nefarius Software Solutions\Vestigo\plugins`. `VESTIGO_SETTINGS_DIR` relocates the default alongside the other state directories.
- `TrustMode`: `Disabled` (default), `AdministratorManaged` (explicitly trusted administrative deployment) or `PinnedSha256` (the complete deployment must match an administrator-configured digest).
- `PackageHashes`: plugin ID to hexadecimal SHA-256 deployment digest, used in pinned mode.

The host never creates or repairs the plugin directory. Provision it through a trusted administrative deployment process with SYSTEM and Administrators as owners/writers, and read/execute access for the service account. Protect all descendants and protect ancestor directories against replacement. Loading validates ACLs and refuses untrusted owners or writers, including writable files inside an otherwise protected tree. Links, junctions and other reparse points anywhere in the deployment or root path are rejected. During development only, `VESTIGO_SETTINGS_DIR` additionally trusts the current account and treats its protected settings directory as the ancestor trust anchor; that account must control the relocated tree. ACL validation currently requires Windows.

Example administrative provisioning after a clean MSI installation (elevated PowerShell):

```powershell
$plugins = Join-Path $env:ProgramData 'Nefarius Software Solutions\Vestigo\plugins'
New-Item -ItemType Directory -Path $plugins -Force
icacls $plugins /inheritance:r
icacls $plugins /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
```

Deploy into a new subdirectory inheriting these ACLs; verify ownership and effective permissions before starting Vestigo. A custom root requires equally protected ancestors. Regular users cannot write these deployments. Root/path-based file uploads, deployment, mutation, trace outputs and process executable paths deny writes/execute access to plugin trees, their ancestors and the machine activation settings, even with broad volume roots and even while plugins are disabled. Reads are still subject to normal roots. These guards do not sandbox administrator-equivalent tools: arbitrary process creation, service reconfiguration, registry/environment changes or privileged third-party installers can indirectly modify executable code. Treat tokens permitting those operations as administrator access; disabling them and restricting access to the service are necessary when callers must not administer plugins.

Pinned mode authenticates the whole package against a digest stored in protected host settings; no commercial signing dependency is required. It is an extensible trust-mode policy, with no Authenticode mode in this release. A signature's mere presence is never accepted as trust. A future publisher policy must verify the chain and explicitly authorized signer, not just detect a signature.

The digest algorithm is SHA-256 over this UTF-8 stream: for each file, ordered by ordinal relative path with `/` separators, append the relative path, NUL, uppercase hexadecimal SHA-256 of the file, then LF. Include **every** file, including `plugin.json`, `.deps.json`, symbols and native assets. Empty directories do not contribute. Compute the digest only after completing the deployment, with the service stopped. The digest changes on any path or content change. Keep the expected digest out of the plugin directory itself.

```powershell
$deployment = (Resolve-Path '.\published-plugin').Path
$paths = [string[]](Get-ChildItem -LiteralPath $deployment -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($deployment, $_.FullName).Replace('\', '/')
})
[Array]::Sort($paths, [StringComparer]::Ordinal)
$hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
foreach ($relative in $paths) {
    $fileHash = (Get-FileHash -LiteralPath (Join-Path $deployment $relative) -Algorithm SHA256).Hash
    $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relative + [char]0 + $fileHash + "`n"))
}
[Convert]::ToHexString($hash.GetHashAndReset())
$hash.Dispose()
```

## Build a plugin

See `examples/Vestigo.ExamplePlugin`, which registers `EchoService` from a separate private assembly, exposes `example_plugin_echo`, and maps `/plugins/example-plugin/status`. It references the contracts project rather than the server. For an independent repository, first pack the public contracts, copy the resulting package to your local package source, and replace that project reference with `PackageReference Include="Vestigo.Abstractions" Version="1.1.0"`. No public NuGet publication of the new contract is assumed. Add a matching released `ModelContextProtocol.AspNetCore` package (currently 2.2.0) and `FrameworkReference Include="Microsoft.AspNetCore.App"`. Set `TargetFramework` to `net10.0` (or the host's Windows TFM if required) and `EnableDynamicLoading` to `true`; exclude runtime copies of the shared contract.

```powershell
dotnet pack server/Vestigo.Abstractions -c Release -o artifacts/contracts
dotnet publish examples/Vestigo.ExamplePlugin -c Release -o artifacts/example-plugin
```

Copy the **published output**, including private dependencies, `.deps.json`, any generated runtime configuration, manifest and `runtimes` assets, to one immediate subdirectory of the protected root. `.runtimeconfig.json` is not required for a framework-dependent class library; the host supplies the runtime. Build output alone and an isolated entry DLL are insufficient. Plugin deployment contents must not overwrite public host files.

## API 1.1 tool providers

An enabled module can register an optional `Vestigo.Extensibility.IVestigoToolProvider`
singleton through its existing service hook. Provider-only modules return `[]` from
`ToolTypes`. Declare `hostApiVersion: "1.1"` in the manifest and reference contracts
package 1.1.0; no server assembly reference is needed. API 1.0 attributed tool classes
remain supported unchanged. The public provider API intentionally uses the shared
MCP SDK's `Tool`, `CallToolRequestParams` and `CallToolResult` types, rather than
introducing another schema or content model.

```csharp
public override void ConfigureServices(IServiceCollection services, PluginContext context)
{
    services.AddSingleton<IVestigoToolProvider, MyToolProvider>();
}
```

Implement `Prefix`, optional `RequiresSessionIdentity` (default false),
`ListToolsAsync(PluginToolContext, CancellationToken)`, and
`CallToolAsync(CallToolRequestParams, PluginToolContext, CancellationToken)`.
The host requires unkeyed singleton registrations from enabled module hooks.
Constructor/factory-created services follow normal DI disposal ownership; a plugin
registering a preconstructed instance is responsible for its lifetime. Register an
`IHostedService` for asynchronous backend startup, readiness and shutdown; do not
perform network I/O in plugin discovery constructors or build another provider.

Prefixes are exclusive, case-sensitive namespaces of 2–32 characters: a lowercase
letter, optional lowercase letters/digits/underscores, and a trailing underscore
(for example `example_`). Duplicate or overlapping prefixes, or a prefix covering
any enabled attributed tool name, abort startup. Providers must not replace the
host's MCP list/call handlers. Multiple providers compose with the built-in catalog
on the same authenticated, stateless `/mcp` endpoint.

Return the complete current catalog with unprefixed names. Names must contain only
letters, digits, `_`, `-` or `.`, be unique within the provider, and fit the MCP
128-character limit after prefixing; input schemas must be JSON objects. The host
clones descriptors and changes only their names, retaining input/output schemas,
annotations, titles, icons and metadata supported by the shared SDK. Catalogs are
validated atomically on each list and before each call. Removed tools cannot be
invoked through a stale advertised name. Independent provider cursors are not
combined: the composed catalog is returned without pagination, and supplied list
cursors are rejected. Clients must re-list to observe schema changes; stateless
transport does not push unsolicited list-change notifications.

Calls receive the unprefixed name, unchanged arguments and protocol metadata except
the host-owned `vestigo/sessionId` field. Providers adapting another MCP server must
handle that server's protocol negotiation, request metadata and resource links.
The host returns the complete `CallToolResult`, including structured content,
images, other SDK-supported content, metadata and `IsError`. Provider exceptions
become sanitized protocol errors; raw exception messages/inner exceptions are not
included. Cancellation is passed through and calls are never automatically retried.
Providers must bound their own I/O, output and backend resources, honor cancellation,
and decide whether their protocol-level errors belong in an intentional tool result.

## Authenticated application session identity

API 1.1 supplies `PluginToolContext.OwnerId` and nullable `SessionIdentity` to each
provider request. Every configured bearer credential has a distinct, random opaque
owner ID for this host process. It is not a token, token digest, username, client
name or remote IP; it changes after restart. Reusing a credential reuses its owner
ID. Existing tokens retain the same global tool permissions; this adds no scopes.

Select an application session using either the HTTP `X-Vestigo-Session-Id` header
or `_meta["vestigo/sessionId"]` in the MCP request parameters. Use the same value
for listing and calling that session's tools. IDs contain 16–128 letters, digits,
underscores or hyphens; a random UUID in `N` format is suitable. Repeated headers,
malformed/non-string IDs, conflicting header/metadata values and unauthenticated
session selections are rejected. Matching header and metadata values are accepted.

```json
{
  "name": "example_echo",
  "arguments": { "value": "hello" },
  "_meta": { "vestigo/sessionId": "0123456789abcdef0123456789abcdef" }
}
```

This is a tool-call parameter example; the client SDK still supplies the negotiated
MCP protocol envelope and any protocol-required metadata/headers. The application
ID is not `Mcp-Session-Id` and does not enable stateful transport. A missing ID is
allowed for providers that do not require one. For `RequiresSessionIdentity=true`,
tools are hidden without an authenticated selection and calls fail closed with
`session_identity_required`. An open host with no tokens cannot establish such an
identity. Built-in tools and existing API 1.0 tools retain their existing behavior.

Use the entire immutable `PluginSessionIdentity(OwnerId, SessionId)` value as the
provider's isolation key, never `SessionId` alone. Different credentials selecting
the same ID get separate keys; one credential selecting different IDs also gets
separate keys. Two agents sharing both a credential and ID are indistinguishable
and deliberately share a key. Give independent agents distinct credentials and/or
IDs. IDs select an identity only: there is no core session creation API, session
store, lease, browser launch, backend connection manager or per-session permission
grant. Each provider must own resource allocation, expiration, synchronization and
cleanup, and protect all operations using the full owner/session key. Owner IDs
changing on restart must not accidentally reconnect to another owner's resources.

No Playwright implementation or interactive process-launch API is included. These
are generic extension points for separately deployed providers, including future
MCP gateway plugins.

## AI-assisted plugin development

Plugin authors can copy [`examples/AGENTS.md`](../examples/AGENTS.md) into the root
of their own plugin repository to give coding agents the public API, packaging,
authorization, trust and validation requirements. Follow the target host release's
contracts and guide when using the template outside this checkout. The
[scoped Cursor rule](../.cursor/rules/vestigo-plugin-development.mdc) applies the
same guidance to examples, public contracts and plugin fixtures in this repository.
Keep any additional corporate instructions and private code in the private repository.

## Manifest and activation

Each immediate plugin subdirectory requires this strict `plugin.json` (UTF-8, at most 64 KiB). Unknown, missing and duplicate fields are rejected:

```json
{
  "id": "example-plugin",
  "version": "1.0.0",
  "hostApiVersion": "1.0",
  "entryAssembly": "Vestigo.ExamplePlugin.dll",
  "entryType": "Vestigo.ExamplePlugin.Plugin"
}
```

IDs contain 1–64 lowercase letters, digits or hyphens, starting with a letter. Version is a numeric .NET version. The entry assembly is a DLL filename directly in this subdirectory; absolute paths, nested paths, traversal and alternate data streams are rejected. Duplicate IDs reject all conflicting deployments. There is no filesystem-wide reflection discovery.

Enable loading separately from enabling modules in the existing protected `appsettings.json`:

```json
{
  "Vestigo": {
    "Plugins": {
      "Enabled": true,
      "TrustMode": "AdministratorManaged"
    },
    "Modules": {
      "ExampleEcho": { "Enabled": true }
    }
  }
}
```

External modules always default to disabled, even if a plugin overrides `EnabledByDefault`. Loading an installed trusted plugin still executes its entry point and module constructors; module enablement controls service/tool/HTTP activation, not trust. One plugin can contain independently enabled modules. To prevent any plugin code from running, disable loading or remove its deployment while stopped. Unknown configured module names remain errors, including a module from a rejected or removed plugin. Remove its configuration deliberately when uninstalling it.

## Authentication and failure policy

External tools use the host's authentication/authorization filters and global token model. Every valid API token can invoke every enabled plugin tool; there are no per-plugin scopes in this release. Corporate deployments needing different audiences must separate hosts or implement additional authorization in trusted plugin code. HTTP endpoints inherit the host fallback policy and route-group authorization; `AllowAnonymous` endpoints are rejected at startup. Configure API tokens before exposing sensitive plugins. With no tokens, plugin endpoints follow the host's open-server behavior, and declared capabilities produce prominent warnings.

Diagnostics distinguish `disabled`, `absent`, `rejected`, `failed` entry points and `loaded` deployments, reporting version, API and module names. Disabled modules appear only among discovered modules, not enabled modules. Rejected/failed plugins contribute no modules and the host may continue if its configuration does not require them. Explicit activation of an unavailable module fails startup. Module/tool collisions, dependency errors, service-registration failures, DI constructor failures and endpoint-policy violations abort the entire startup, before any request is served. Arbitrary plugin side effects cannot be rolled back; there is no attempt to continue after partially running an activation hook. Restart after correcting the deployment. Plugin exception messages are suppressed to avoid leaking secrets.

For assembly failures, check the entry filename/type, API requirement, published `.deps.json`, private dependency files, shared SDK major/version, process architecture and reparse/ACL diagnostics. Do not copy newer shared SDK contracts into the plugin to bypass compatibility checks. For MCP failures, inspect explicitly assigned tool names and the SDK's resolved default names, constructor registrations and enabled module dependencies. Duplicate C# type names do not define MCP identity. Plugin deployment and module activation require a service restart; an enabled API 1.1 provider can update its catalog on subsequent requests.

## MSI, updates and validation

The public publish/MSI payload contains the contracts library and built-in host only. Examples and test plugins are separate projects, never runtime dependencies of the server. Private binaries and feeds are not referenced by the public build. Installer module selection remains the explicit built-in list; private modules are configured by their administrator. Existing MSI upgrades and the public updater replace the application payload, while plugins remain outside that directory in ProgramData. Installer settings merge preserves unknown plugin/module keys. No public updater downloads or installs plugin code.

Stop the service, stage and authenticate a versioned private deployment, atomically replace the plugin directory where practical, update its protected hash pin if needed, then restart. Verify startup diagnostics, MCP discovery/invocation and the plugin status endpoint. Keep the previous package available for rollback. Host API requirements remain in each manifest for machine-readable compatibility checks.

The server test project publishes the example and versioned test fixtures before running. Tests load those artifacts in real contexts and cover private version coexistence, native RID assets, shared identity, manifest rejection, cross-module dependencies, pins, ACL rejection, junctions, authentication and MCP echo invocation. The deployment smoke script requires PowerShell 7.4 or later and bounds connection and response reads. `tests/Test-ExternalPluginDeployment.ps1 -HostPublishDirectory <published-win-x64-host> -PluginPublishDirectory <published-example-plugin>` additionally launches an isolated published executable and verifies health, HTTP authentication, MCP discovery and echo invocation. It can use a plugin built outside the repository from the packed contracts NuGet. It retains its protected temporary state directory for diagnostics; it never installs a service or changes machine configuration. Windows service installation and MSI upgrade smoke tests still require an elevated deployment environment; they do not run merely by starting the in-process test host.
