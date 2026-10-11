# IRealtimeTraceSession

Namespace: Vestigo.Etws

One realtime ETW session owned by a capture job.

```csharp
public interface IRealtimeTraceSession : System.IAsyncDisposable
```

Implements [IAsyncDisposable](https://learn.microsoft.com/dotnet/api/system.iasyncdisposable)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute)

## Properties

### <a id="properties-sessionname"/>**SessionName**

```csharp
public abstract string SessionName { get; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-enableprovider"/>**EnableProvider(Guid, TraceLevelSelection, UInt64, UInt64)**

```csharp
void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword)
```

#### Parameters

`providerId` [Guid](https://learn.microsoft.com/dotnet/api/system.guid)<br>

`level` [TraceLevelSelection](./vestigo.etws.tracelevelselection.md)<br>

`matchAnyKeyword` [UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

`matchAllKeyword` [UInt64](https://learn.microsoft.com/dotnet/api/system.uint64)<br>

### <a id="methods-readeventsasync"/>**ReadEventsAsync(DecodingContext, Boolean, Action, CancellationToken)**

```csharp
IAsyncEnumerable<ReadOnlyMemory<Byte>> ReadEventsAsync(DecodingContext context, bool rewriteProviderName, Action onMissingFormat, CancellationToken cancellationToken)
```

#### Parameters

`context` DecodingContext<br>

`rewriteProviderName` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`onMissingFormat` [Action](https://learn.microsoft.com/dotnet/api/system.action)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[IAsyncEnumerable](https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1)<[ReadOnlyMemory](https://learn.microsoft.com/dotnet/api/system.readonlymemory-1)<[Byte](https://learn.microsoft.com/dotnet/api/system.byte)>>
