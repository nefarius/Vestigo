# CaptureStatus

Namespace: Vestigo.Etws

Status of one managed realtime capture.

```csharp
public record CaptureStatus
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [CaptureStatus](./vestigo.etws.capturestatus.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-bytecount"/>**ByteCount**

```csharp
public long ByteCount { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

### <a id="properties-error"/>**Error**

```csharp
public string Error { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-eventcount"/>**EventCount**

```csharp
public long EventCount { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

### <a id="properties-id"/>**Id**

```csharp
public string Id { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-missingformatcount"/>**MissingFormatCount**

```csharp
public int MissingFormatCount { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-sessionname"/>**SessionName**

```csharp
public string SessionName { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-startedat"/>**StartedAt**

```csharp
public DateTimeOffset StartedAt { get; set; }
```

#### Property Value

[DateTimeOffset](https://learn.microsoft.com/dotnet/api/system.datetimeoffset)<br>

### <a id="properties-state"/>**State**

```csharp
public string State { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-stoppedat"/>**StoppedAt**

```csharp
public Nullable<DateTimeOffset> StoppedAt { get; set; }
```

#### Property Value

[Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[DateTimeOffset](https://learn.microsoft.com/dotnet/api/system.datetimeoffset)><br>

### <a id="properties-stopreason"/>**StopReason**

```csharp
public string StopReason { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Constructors

### <a id="constructors-.ctor"/>**CaptureStatus(String, String, String, Int64, Int64, Int32, DateTimeOffset, Nullable&lt;DateTimeOffset&gt;, String, String)**

Status of one managed realtime capture.

```csharp
public CaptureStatus(string Id, string SessionName, string State, long EventCount, long ByteCount, int MissingFormatCount, DateTimeOffset StartedAt, Nullable<DateTimeOffset> StoppedAt, string StopReason, string Error)
```

#### Parameters

`Id` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`SessionName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`State` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`EventCount` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`ByteCount` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`MissingFormatCount` [Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

`StartedAt` [DateTimeOffset](https://learn.microsoft.com/dotnet/api/system.datetimeoffset)<br>

`StoppedAt` [Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[DateTimeOffset](https://learn.microsoft.com/dotnet/api/system.datetimeoffset)><br>

`StopReason` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Error` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-equals"/>**Equals(CaptureStatus)**

```csharp
public bool Equals(CaptureStatus other)
```

#### Parameters

`other` [CaptureStatus](./vestigo.etws.capturestatus.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
