# ProviderDescription

Namespace: Vestigo.Etws

One WPP control GUID declared by a PDB.

```csharp
public record ProviderDescription
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [ProviderDescription](./vestigo.etws.providerdescription.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-bitflags"/>**BitFlags**

```csharp
public IReadOnlyList<String> BitFlags { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-controlguid"/>**ControlGuid**

```csharp
public Guid ControlGuid { get; set; }
```

#### Property Value

[Guid](https://learn.microsoft.com/dotnet/api/system.guid)<br>

### <a id="properties-name"/>**Name**

```csharp
public string Name { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-symbolfile"/>**SymbolFile**

```csharp
public string SymbolFile { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Constructors

### <a id="constructors-.ctor"/>**ProviderDescription(Guid, String, IReadOnlyList&lt;String&gt;, String)**

One WPP control GUID declared by a PDB.

```csharp
public ProviderDescription(Guid ControlGuid, string Name, IReadOnlyList<String> BitFlags, string SymbolFile)
```

#### Parameters

`ControlGuid` [Guid](https://learn.microsoft.com/dotnet/api/system.guid)<br>

`Name` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`BitFlags` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`SymbolFile` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-equals"/>**Equals(ProviderDescription)**

```csharp
public bool Equals(ProviderDescription other)
```

#### Parameters

`other` [ProviderDescription](./vestigo.etws.providerdescription.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
