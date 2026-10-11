# ApiTokenAuthenticationHandler

Namespace: Vestigo.Security

Authenticates `Authorization: Bearer` against the configured API tokens.

```csharp
public sealed class ApiTokenAuthenticationHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>, Microsoft.AspNetCore.Authentication.IAuthenticationHandler
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [AuthenticationHandler](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationhandler-1)<[AuthenticationSchemeOptions](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationschemeoptions)> → [ApiTokenAuthenticationHandler](./vestigo.security.apitokenauthenticationhandler.md)<br>
Implements [IAuthenticationHandler](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.iauthenticationhandler)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-options"/>**Options**

```csharp
public AuthenticationSchemeOptions Options { get; }
```

#### Property Value

[AuthenticationSchemeOptions](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationschemeoptions)<br>

### <a id="properties-scheme"/>**Scheme**

```csharp
public AuthenticationScheme Scheme { get; }
```

#### Property Value

[AuthenticationScheme](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationscheme)<br>

## Constructors

### <a id="constructors-.ctor"/>**ApiTokenAuthenticationHandler(IOptionsMonitor&lt;AuthenticationSchemeOptions&gt;, ILoggerFactory, UrlEncoder, VestigoOptions)**

Authenticates `Authorization: Bearer` against the configured API tokens.

```csharp
public ApiTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, VestigoOptions vestigo)
```

#### Parameters

`options` [IOptionsMonitor](https://learn.microsoft.com/dotnet/api/microsoft.extensions.options.ioptionsmonitor-1)<[AuthenticationSchemeOptions](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationschemeoptions)><br>

`logger` [ILoggerFactory](https://learn.microsoft.com/dotnet/api/microsoft.extensions.logging.iloggerfactory)<br>

`encoder` [UrlEncoder](https://learn.microsoft.com/dotnet/api/system.text.encodings.web.urlencoder)<br>

`vestigo` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

## Methods

### <a id="methods-handleauthenticateasync"/>**HandleAuthenticateAsync()**

```csharp
protected Task<AuthenticateResult> HandleAuthenticateAsync()
```

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[AuthenticateResult](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticateresult)>

### <a id="methods-handlechallengeasync"/>**HandleChallengeAsync(AuthenticationProperties)**

```csharp
protected Task HandleChallengeAsync(AuthenticationProperties properties)
```

#### Parameters

`properties` [AuthenticationProperties](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.authenticationproperties)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task)
