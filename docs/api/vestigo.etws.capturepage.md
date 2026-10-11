# CapturePage

Namespace: Vestigo.Etws

One page of spooled NDJSON events.

```csharp
public record CapturePage
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [CapturePage](./vestigo.etws.capturepage.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-events"/>**Events**

```csharp
public IReadOnlyList<String> Events { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-id"/>**Id**

```csharp
public string Id { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-nextsequence"/>**NextSequence**

```csharp
public long NextSequence { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

## Constructors

### <a id="constructors-.ctor"/>**CapturePage(String, Int64, IReadOnlyList&lt;String&gt;)**

One page of spooled NDJSON events.

```csharp
public CapturePage(string Id, long NextSequence, IReadOnlyList<String> Events)
```

#### Parameters

`Id` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`NextSequence` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`Events` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

## Methods

### <a id="methods-equals"/>**Equals(CapturePage)**

```csharp
public bool Equals(CapturePage other)
```

#### Parameters

`other` [CapturePage](./vestigo.etws.capturepage.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
