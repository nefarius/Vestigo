# PdbReferenceDescription

Namespace: Vestigo.Etws

A PDB identity referenced by an ETL file.

```csharp
public record PdbReferenceDescription
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [PdbReferenceDescription](./vestigo.etws.pdbreferencedescription.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-age"/>**Age**

```csharp
public int Age { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-guid"/>**Guid**

```csharp
public Guid Guid { get; set; }
```

#### Property Value

[Guid](https://learn.microsoft.com/dotnet/api/system.guid)<br>

### <a id="properties-indexprefix"/>**IndexPrefix**

```csharp
public string IndexPrefix { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-pdbname"/>**PdbName**

```csharp
public string PdbName { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Constructors

### <a id="constructors-.ctor"/>**PdbReferenceDescription(String, Guid, Int32, String)**

A PDB identity referenced by an ETL file.

```csharp
public PdbReferenceDescription(string PdbName, Guid Guid, int Age, string IndexPrefix)
```

#### Parameters

`PdbName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Guid` [Guid](https://learn.microsoft.com/dotnet/api/system.guid)<br>

`Age` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`IndexPrefix` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-equals"/>**Equals(PdbReferenceDescription)**

```csharp
public bool Equals(PdbReferenceDescription other)
```

#### Parameters

`other` [PdbReferenceDescription](./vestigo.etws.pdbreferencedescription.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
