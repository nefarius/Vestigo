# SymbolCatalogService

Namespace: Vestigo.Etws

Inspects PDBs, loads decoding sources, and builds the configured symbol store.

```csharp
public sealed class SymbolCatalogService
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [SymbolCatalogService](./vestigo.etws.symbolcatalogservice.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Constructors

### <a id="constructors-.ctor"/>**SymbolCatalogService(VestigoOptions, PathPolicy, IHttpClientFactory)**

Inspects PDBs, loads decoding sources, and builds the configured symbol store.

```csharp
public SymbolCatalogService(VestigoOptions options, PathPolicy paths, IHttpClientFactory httpClientFactory)
```

#### Parameters

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

`paths` [PathPolicy](./vestigo.security.pathpolicy.md)<br>

`httpClientFactory` [IHttpClientFactory](https://learn.microsoft.com/dotnet/api/system.net.http.ihttpclientfactory)<br>

## Methods

### <a id="methods-createstore"/>**CreateStore()**

```csharp
public SymbolStoreConfiguration CreateStore()
```

#### Returns

SymbolStoreConfiguration

### <a id="methods-createsymbolclient"/>**CreateSymbolClient()**

```csharp
public HttpClient CreateSymbolClient()
```

#### Returns

[HttpClient](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient)

### <a id="methods-inspectpdb"/>**InspectPdb(String)**

```csharp
public OperationResult<PdbInspection> InspectPdb(string pdbPath)
```

#### Parameters

`pdbPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[PdbInspection](./vestigo.etws.pdbinspection.md)>

### <a id="methods-loadexplicit"/>**LoadExplicit(IReadOnlyList&lt;String&gt;)**

```csharp
public OperationResult<DecodingSourceSet> LoadExplicit(IReadOnlyList<String> symbolPaths)
```

#### Parameters

`symbolPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<DecodingSourceSet>

### <a id="methods-scanetl"/>**ScanEtl(IReadOnlyList&lt;String&gt;)**

```csharp
public OperationResult<IReadOnlyList<PdbReferenceDescription>> ScanEtl(IReadOnlyList<String> etlPaths)
```

#### Parameters

`etlPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[PdbReferenceDescription](./vestigo.etws.pdbreferencedescription.md)>>

### <a id="methods-validatesources"/>**ValidateSources(IReadOnlyList&lt;String&gt;)**

```csharp
public OperationResult<SymbolLoadDescription> ValidateSources(IReadOnlyList<String> symbolPaths)
```

#### Parameters

`symbolPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[SymbolLoadDescription](./vestigo.etws.symbolloaddescription.md)>
