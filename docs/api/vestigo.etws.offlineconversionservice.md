# OfflineConversionService

Namespace: Vestigo.Etws

Converts ETL files to JSON or NDJSON inside the configured output roots.

```csharp
public sealed class OfflineConversionService
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [OfflineConversionService](./vestigo.etws.offlineconversionservice.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Constructors

### <a id="constructors-.ctor"/>**OfflineConversionService(VestigoOptions, PathPolicy, SymbolCatalogService)**

Converts ETL files to JSON or NDJSON inside the configured output roots.

```csharp
public OfflineConversionService(VestigoOptions options, PathPolicy paths, SymbolCatalogService symbols)
```

#### Parameters

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

`paths` [PathPolicy](./vestigo.security.pathpolicy.md)<br>

`symbols` [SymbolCatalogService](./vestigo.etws.symbolcatalogservice.md)<br>

## Methods

### <a id="methods-convertasync"/>**ConvertAsync(IReadOnlyList&lt;String&gt;, String, String, IReadOnlyList&lt;String&gt;, Boolean, Boolean, CancellationToken)**

```csharp
public Task<OperationResult<ConversionSummary>> ConvertAsync(IReadOnlyList<String> etlPaths, string outputPath, string format, IReadOnlyList<String> symbolPaths, bool resolveSymbols, bool overwrite, CancellationToken cancellationToken)
```

#### Parameters

`etlPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`outputPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`format` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`symbolPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`resolveSymbols` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`overwrite` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[ConversionSummary](./vestigo.etws.conversionsummary.md)>>
