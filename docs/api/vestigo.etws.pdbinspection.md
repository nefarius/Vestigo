# PdbInspection

Namespace: Vestigo.Etws

WPP controls found in one PDB.

```csharp
public record PdbInspection
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [PdbInspection](./vestigo.etws.pdbinspection.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-path"/>**Path**

```csharp
public string Path { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-providers"/>**Providers**

```csharp
public IReadOnlyList<ProviderDescription> Providers { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[ProviderDescription](./vestigo.etws.providerdescription.md)><br>

## Constructors

### <a id="constructors-.ctor"/>**PdbInspection(String, IReadOnlyList&lt;ProviderDescription&gt;)**

WPP controls found in one PDB.

```csharp
public PdbInspection(string Path, IReadOnlyList<ProviderDescription> Providers)
```

#### Parameters

`Path` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Providers` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[ProviderDescription](./vestigo.etws.providerdescription.md)><br>

## Methods

### <a id="methods-equals"/>**Equals(PdbInspection)**

```csharp
public bool Equals(PdbInspection other)
```

#### Parameters

`other` [PdbInspection](./vestigo.etws.pdbinspection.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
