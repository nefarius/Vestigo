# TraceTools

Namespace: Vestigo.Tools

MCP tools for offline WPP conversion and managed realtime capture.

```csharp
public sealed class TraceTools
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [TraceTools](./vestigo.tools.tracetools.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute), McpServerToolTypeAttribute

## Constructors

### <a id="constructors-.ctor"/>**TraceTools(SymbolCatalogService, OfflineConversionService, CaptureManager, IRealtimeTraceSessionFactory)**

MCP tools for offline WPP conversion and managed realtime capture.

```csharp
public TraceTools(SymbolCatalogService symbols, OfflineConversionService offline, CaptureManager captures, IRealtimeTraceSessionFactory sessions)
```

#### Parameters

`symbols` [SymbolCatalogService](./vestigo.etws.symbolcatalogservice.md)<br>

`offline` [OfflineConversionService](./vestigo.etws.offlineconversionservice.md)<br>

`captures` [CaptureManager](./vestigo.etws.capturemanager.md)<br>

`sessions` [IRealtimeTraceSessionFactory](./vestigo.etws.irealtimetracesessionfactory.md)<br>

## Methods

### <a id="methods-convertetl"/>**ConvertEtl(String[], String, String, String[], Boolean, Boolean, CancellationToken)**

```csharp
public Task<OperationResult<ConversionSummary>> ConvertEtl(String[] etlPaths, string outputPath, string format, String[] symbolPaths, bool resolveSymbols, bool overwrite, CancellationToken cancellationToken)
```

#### Parameters

`etlPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

`outputPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`format` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`symbolPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

`resolveSymbols` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`overwrite` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[ConversionSummary](./vestigo.etws.conversionsummary.md)>>

### <a id="methods-getcapture"/>**GetCapture(String)**

```csharp
public OperationResult<CaptureStatus> GetCapture(string captureId)
```

#### Parameters

`captureId` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>

### <a id="methods-inspectwpppdb"/>**InspectWppPdb(String)**

```csharp
public OperationResult<PdbInspection> InspectWppPdb(string pdbPath)
```

#### Parameters

`pdbPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[PdbInspection](./vestigo.etws.pdbinspection.md)>

### <a id="methods-listcaptures"/>**ListCaptures()**

```csharp
public IReadOnlyList<CaptureStatus> ListCaptures()
```

#### Returns

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[CaptureStatus](./vestigo.etws.capturestatus.md)>

### <a id="methods-listetwsessions"/>**ListEtwSessions()**

```csharp
public OperationResult<IReadOnlyList<String>> ListEtwSessions()
```

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)>>

### <a id="methods-readcapture"/>**ReadCapture(String, Int64, Nullable&lt;Int32&gt;)**

```csharp
public OperationResult<CapturePage> ReadCapture(string captureId, long afterSequence, Nullable<Int32> limit)
```

#### Parameters

`captureId` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`afterSequence` [Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

`limit` [Nullable](https://learn.microsoft.com/dotnet/api/system.nullable-1)<[Int32](https://learn.microsoft.com/dotnet/api/system.int32)><br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[CapturePage](./vestigo.etws.capturepage.md)>

### <a id="methods-scanetlmetadata"/>**ScanEtlMetadata(String[])**

```csharp
public OperationResult<IReadOnlyList<PdbReferenceDescription>> ScanEtlMetadata(String[] etlPaths)
```

#### Parameters

`etlPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[PdbReferenceDescription](./vestigo.etws.pdbreferencedescription.md)>>

### <a id="methods-startcapture"/>**StartCapture(String[], String[], String[], String, String, String, String, CancellationToken)**

```csharp
public Task<OperationResult<CaptureStatus>> StartCapture(String[] pdbPaths, String[] tmfPaths, String[] providerIds, string level, string matchAnyKeyword, string matchAllKeyword, string duration, CancellationToken cancellationToken)
```

#### Parameters

`pdbPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

`tmfPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

`providerIds` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

`level` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`matchAnyKeyword` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`matchAllKeyword` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`duration` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>>

### <a id="methods-stopcapture"/>**StopCapture(String)**

```csharp
public Task<OperationResult<CaptureStatus>> StopCapture(string captureId)
```

#### Parameters

`captureId` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[Task](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1)<[OperationResult](./vestigo.etws.operationresult-1.md)<[CaptureStatus](./vestigo.etws.capturestatus.md)>>

### <a id="methods-validatedecodingsources"/>**ValidateDecodingSources(String[])**

```csharp
public OperationResult<SymbolLoadDescription> ValidateDecodingSources(String[] symbolPaths)
```

#### Parameters

`symbolPaths` [String[]](https://learn.microsoft.com/dotnet/api/system.string[])<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<[SymbolLoadDescription](./vestigo.etws.symbolloaddescription.md)>
