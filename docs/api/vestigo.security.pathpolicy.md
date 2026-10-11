# PathPolicy

Namespace: Vestigo.Security

Canonicalizes client paths and confines them to configured roots.

```csharp
public sealed class PathPolicy
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [PathPolicy](./vestigo.security.pathpolicy.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Constructors

### <a id="constructors-.ctor"/>**PathPolicy(VestigoOptions)**

Canonicalizes client paths and confines them to configured roots.

```csharp
public PathPolicy(VestigoOptions options)
```

#### Parameters

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

## Methods

### <a id="methods-requireexistingfile"/>**RequireExistingFile(String, IReadOnlyCollection&lt;String&gt;)**

```csharp
public PathCheck RequireExistingFile(string requestedPath, IReadOnlyCollection<String> extensions)
```

#### Parameters

`requestedPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`extensions` [IReadOnlyCollection](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlycollection-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[PathCheck](./vestigo.security.pathcheck.md)

### <a id="methods-requireexistingfiles"/>**RequireExistingFiles(IEnumerable&lt;String&gt;, IReadOnlyCollection&lt;String&gt;)**

```csharp
public IReadOnlyList<PathCheck> RequireExistingFiles(IEnumerable<String> requestedPaths, IReadOnlyCollection<String> extensions)
```

#### Parameters

`requestedPaths` [IEnumerable](https://learn.microsoft.com/dotnet/api/system.collections.generic.ienumerable-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`extensions` [IReadOnlyCollection](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlycollection-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[PathCheck](./vestigo.security.pathcheck.md)>

### <a id="methods-requireoutputfile"/>**RequireOutputFile(String, IReadOnlyCollection&lt;String&gt;, Boolean)**

```csharp
public PathCheck RequireOutputFile(string requestedPath, IReadOnlyCollection<String> extensions, bool overwrite)
```

#### Parameters

`requestedPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`extensions` [IReadOnlyCollection](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlycollection-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`overwrite` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

#### Returns

[PathCheck](./vestigo.security.pathcheck.md)
