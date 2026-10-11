# SymbolLoadDescription

Namespace: Vestigo.Etws

Result of loading explicit PDB and TMF decoding sources.

```csharp
public record SymbolLoadDescription
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [SymbolLoadDescription](./vestigo.etws.symbolloaddescription.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-canrewriteprovidernames"/>**CanRewriteProviderNames**

```csharp
public bool CanRewriteProviderNames { get; set; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

### <a id="properties-diagnostics"/>**Diagnostics**

```csharp
public IReadOnlyList<String> Diagnostics { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-sourcecount"/>**SourceCount**

```csharp
public int SourceCount { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

## Constructors

### <a id="constructors-.ctor"/>**SymbolLoadDescription(Int32, Boolean, IReadOnlyList&lt;String&gt;)**

Result of loading explicit PDB and TMF decoding sources.

```csharp
public SymbolLoadDescription(int SourceCount, bool CanRewriteProviderNames, IReadOnlyList<String> Diagnostics)
```

#### Parameters

`SourceCount` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`CanRewriteProviderNames` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`Diagnostics` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

## Methods

### <a id="methods-equals"/>**Equals(SymbolLoadDescription)**

```csharp
public bool Equals(SymbolLoadDescription other)
```

#### Parameters

`other` [SymbolLoadDescription](./vestigo.etws.symbolloaddescription.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
