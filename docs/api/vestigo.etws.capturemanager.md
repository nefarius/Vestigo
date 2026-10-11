# CaptureManager

Namespace: Vestigo.Etws

Owns realtime capture jobs, their spools, and Vestigo-prefixed ETW sessions.

```csharp
public sealed class CaptureManager : Microsoft.Extensions.Hosting.IHostedService, System.IDisposable
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [CaptureManager](./vestigo.etws.capturemanager.md)<br>
Implements [IHostedService](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.ihostedservice), [IDisposable](https://learn.microsoft.com/dotnet/api/system.idisposable)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Fields

### <a id="fields-sessionprefix"/>**SessionPrefix**

```csharp
public static string SessionPrefix;
```

## Constructors

### <a id="constructors-.ctor"/>**CaptureManager(VestigoOptions, SymbolCatalogService, IRealtimeTraceSessionFactory, ILogger&lt;CaptureManager&gt;)**

```csharp
public CaptureManager(VestigoOptions options, SymbolCatalogService symbols, IRealtimeTraceSessionFactory sessions, ILogger<CaptureManager> logger)
```

#### Parameters

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

`symbols` [SymbolCatalogService](./vestigo.etws.symbolcatalogservice.md)<br>

`sessions` [IRealtimeTraceSessionFactory](./vestigo.etws.irealtimetracesessionfactory.md)<br>

`logger` [ILogger](https://learn.microsoft.com/dotnet/api/microsoft.extensions.logging.ilogger-1)<[CaptureManager](./vestigo.etws.capturemanager.md)><br>

## Methods

### <a id="methods-cleanupexpired"/>**CleanupExpired()**

```csharp
public void CleanupExpired()
```

### <a id="methods-dispose"/>**Dispose()**

```csharp
public void Dispose()
```

### <a id="methods-get"/>**Get(String)**

```csharp
public OperationResult<CaptureStatus> Get(string id)
```

#### Parameters

`id` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>

### <a id="methods-list"/>**List()**

```csharp
public IReadOnlyList<CaptureStatus> List()
```

#### Returns

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[CaptureStatus](./vestigo.etws.capturestatus.md)>

### <a id="methods-read"/>**Read(String, Int64, Nullable&lt;Int32&gt;)**

```csharp
public OperationResult<CapturePage> Read(string id, long afterSequence, Nullable<Int32> limit)
```

#### Parameters

`id` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`afterSequence` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`limit` [Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[Int32](https://learn.microsoft.com/dotnet/api/system.int32)><br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[CapturePage](./vestigo.etws.capturepage.md)>

### <a id="methods-startasync"/>**StartAsync(CancellationToken)**

```csharp
public Task StartAsync(CancellationToken cancellationToken)
```

#### Parameters

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task)

### <a id="methods-startasync"/>**StartAsync(StartCaptureRequest, CancellationToken)**

```csharp
public Task<OperationResult<CaptureStatus>> StartAsync(StartCaptureRequest request, CancellationToken cancellationToken)
```

#### Parameters

`request` [StartCaptureRequest](./vestigo.etws.startcapturerequest.md)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>>

### <a id="methods-stopasync"/>**StopAsync(CancellationToken)**

```csharp
public Task StopAsync(CancellationToken cancellationToken)
```

#### Parameters

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task)

### <a id="methods-stopasync"/>**StopAsync(String)**

```csharp
public Task<OperationResult<CaptureStatus>> StopAsync(string id)
```

#### Parameters

`id` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>>
