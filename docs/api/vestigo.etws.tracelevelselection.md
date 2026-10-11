# TraceLevelSelection

Namespace: Vestigo.Etws

ETW trace level, with the same numeric values as the native trace level.

```csharp
public enum TraceLevelSelection
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [ValueType](https://learn.microsoft.com/dotnet/api/system.valuetype) → [Enum](https://learn.microsoft.com/dotnet/api/system.enum) → [TraceLevelSelection](./vestigo.etws.tracelevelselection.md)<br>
Implements [IComparable](https://learn.microsoft.com/dotnet/api/system.icomparable), [ISpanFormattable](https://learn.microsoft.com/dotnet/api/system.ispanformattable), [IFormattable](https://learn.microsoft.com/dotnet/api/system.iformattable), [IConvertible](https://learn.microsoft.com/dotnet/api/system.iconvertible)

## Fields

| Name | Value | Description |
| --- | --: | --- |
| None | 0 | No events. |
| Critical | 1 | Critical events. |
| Error | 2 | Error events. |
| Warning | 3 | Warning events. |
| Information | 4 | Informational events. |
| Verbose | 5 | Verbose events. |
