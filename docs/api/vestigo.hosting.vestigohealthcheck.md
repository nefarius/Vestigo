# VestigoHealthCheck

Namespace: Vestigo.Hosting

Reports spool writability and whether allowed roots are configured.

```csharp
public sealed class VestigoHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [VestigoHealthCheck](./vestigo.hosting.vestigohealthcheck.md)<br>
Implements [IHealthCheck](https://learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.healthchecks.ihealthcheck)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Constructors

### <a id="constructors-.ctor"/>**VestigoHealthCheck(VestigoOptions)**

Reports spool writability and whether allowed roots are configured.

```csharp
public VestigoHealthCheck(VestigoOptions options)
```

#### Parameters

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

## Methods

### <a id="methods-checkhealthasync"/>**CheckHealthAsync(HealthCheckContext, CancellationToken)**

```csharp
public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
```

#### Parameters

`context` [HealthCheckContext](https://learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.healthchecks.healthcheckcontext)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[HealthCheckResult](https://learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.healthchecks.healthcheckresult)>
