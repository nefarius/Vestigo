# StartCaptureRequest

Namespace: Vestigo.Etws

Arguments for starting a managed capture.

```csharp
public record StartCaptureRequest
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [StartCaptureRequest](./vestigo.etws.startcapturerequest.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-duration"/>**Duration**

```csharp
public Nullable<TimeSpan> Duration { get; set; }
```

#### Property Value

[Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[TimeSpan](https://learn.microsoft.com/dotnet/api/system.timespan)><br>

### <a id="properties-level"/>**Level**

```csharp
public string Level { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-matchallkeyword"/>**MatchAllKeyword**

```csharp
public ulong MatchAllKeyword { get; set; }
```

#### Property Value

[UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

### <a id="properties-matchanykeyword"/>**MatchAnyKeyword**

```csharp
public ulong MatchAnyKeyword { get; set; }
```

#### Property Value

[UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

### <a id="properties-pdbpaths"/>**PdbPaths**

```csharp
public IReadOnlyList<String> PdbPaths { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-providerids"/>**ProviderIds**

```csharp
public IReadOnlyList<Guid> ProviderIds { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[Guid](https://learn.microsoft.com/dotnet/api/system.guid)><br>

### <a id="properties-tmfpaths"/>**TmfPaths**

```csharp
public IReadOnlyList<String> TmfPaths { get; set; }
```

#### Property Value

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

## Constructors

### <a id="constructors-.ctor"/>**StartCaptureRequest(IReadOnlyList&lt;String&gt;, IReadOnlyList&lt;String&gt;, IReadOnlyList&lt;Guid&gt;, String, UInt64, UInt64, Nullable&lt;TimeSpan&gt;)**

Arguments for starting a managed capture.

```csharp
public StartCaptureRequest(IReadOnlyList<String> PdbPaths, IReadOnlyList<String> TmfPaths, IReadOnlyList<Guid> ProviderIds, string Level, ulong MatchAnyKeyword, ulong MatchAllKeyword, Nullable<TimeSpan> Duration)
```

#### Parameters

`PdbPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`TmfPaths` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

`ProviderIds` [IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[Guid](https://learn.microsoft.com/dotnet/api/system.guid)><br>

`Level` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`MatchAnyKeyword` [UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

`MatchAllKeyword` [UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

`Duration` [Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[TimeSpan](https://learn.microsoft.com/dotnet/api/system.timespan)><br>

## Methods

### <a id="methods-equals"/>**Equals(StartCaptureRequest)**

```csharp
public bool Equals(StartCaptureRequest other)
```

#### Parameters

`other` [StartCaptureRequest](./vestigo.etws.startcapturerequest.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
