# IRealtimeTraceSessionFactory

Namespace: Vestigo.Etws

Creates and queries realtime ETW sessions. Tests substitute a fake factory.

```csharp
public interface IRealtimeTraceSessionFactory
```

Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute)

## Methods

### <a id="methods-create"/>**Create(String, VestigoOptions)**

```csharp
IRealtimeTraceSession Create(string sessionName, VestigoOptions options)
```

#### Parameters

`sessionName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

#### Returns

[IRealtimeTraceSession](./vestigo.etws.irealtimetracesession.md)

### <a id="methods-listsessions"/>**ListSessions()**

```csharp
IReadOnlyList<String> ListSessions()
```

#### Returns

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)>

### <a id="methods-stoporphan"/>**StopOrphan(String)**

```csharp
void StopOrphan(string sessionName)
```

#### Parameters

`sessionName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>
