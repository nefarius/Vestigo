# ConversionSummary

Namespace: Vestigo.Etws

Summary of one bounded ETL conversion. Event text stays in the output file.

```csharp
public record ConversionSummary
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [ConversionSummary](./vestigo.etws.conversionsummary.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-diagnostics"/>**Diagnostics**

```csharp
public IReadOnlyList<String> Diagnostics { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-eventcount"/>**EventCount**

```csharp
public int EventCount { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-format"/>**Format**

```csharp
public string Format { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-missingformatcount"/>**MissingFormatCount**

```csharp
public int MissingFormatCount { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-outputbytes"/>**OutputBytes**

```csharp
public long OutputBytes { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

### <a id="properties-outputpath"/>**OutputPath**

```csharp
public string OutputPath { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-resolvedsymbolcount"/>**ResolvedSymbolCount**

```csharp
public int ResolvedSymbolCount { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-truncated"/>**Truncated**

```csharp
public bool Truncated { get; set; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

## Constructors

### <a id="constructors-.ctor"/>**ConversionSummary(String, String, Int32, Int64, Int32, Boolean, Int32, IReadOnlyList&lt;String&gt;)**

Summary of one bounded ETL conversion. Event text stays in the output file.

```csharp
public ConversionSummary(string OutputPath, string Format, int EventCount, long OutputBytes, int MissingFormatCount, bool Truncated, int ResolvedSymbolCount, IReadOnlyList<String> Diagnostics)
```

#### Parameters

`OutputPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Format` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`EventCount` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`OutputBytes` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`MissingFormatCount` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`Truncated` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`ResolvedSymbolCount` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`Diagnostics` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

## Methods

### <a id="methods-equals"/>**Equals(ConversionSummary)**

```csharp
public bool Equals(ConversionSummary other)
```

#### Parameters

`other` [ConversionSummary](./vestigo.etws.conversionsummary.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
