# EtwRealtimeTraceSessionFactory

Namespace: Vestigo.Etws

Realtime session factory backed by `Nefarius.Utilities.ETW`.

```csharp
public sealed class EtwRealtimeTraceSessionFactory : IRealtimeTraceSessionFactory
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [EtwRealtimeTraceSessionFactory](./vestigo.etws.etwrealtimetracesessionfactory.md)<br>
Implements [IRealtimeTraceSessionFactory](./vestigo.etws.irealtimetracesessionfactory.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Constructors

### <a id="constructors-.ctor"/>**EtwRealtimeTraceSessionFactory()**

```csharp
public EtwRealtimeTraceSessionFactory()
```

## Methods

### <a id="methods-create"/>**Create(String, VestigoOptions)**

```csharp
public IRealtimeTraceSession Create(string sessionName, VestigoOptions options)
```

#### Parameters

`sessionName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`options` [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>

#### Returns

[IRealtimeTraceSession](./vestigo.etws.irealtimetracesession.md)

### <a id="methods-listsessions"/>**ListSessions()**

```csharp
public IReadOnlyList<String> ListSessions()
```

#### Returns

[IReadOnlyList](https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)>

### <a id="methods-stoporphan"/>**StopOrphan(String)**

```csharp
public void StopOrphan(string sessionName)
```

#### Parameters

`sessionName` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>
