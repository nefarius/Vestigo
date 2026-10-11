# ApiTokenVerifier

Namespace: Vestigo.Security

Compares bearer tokens with SHA-256 and a fixed-time equality check.

```csharp
public static class ApiTokenVerifier
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [ApiTokenVerifier](./vestigo.security.apitokenverifier.md)

## Methods

### <a id="methods-matches"/>**Matches(String, IEnumerable&lt;String&gt;)**

```csharp
public static bool Matches(string provided, IEnumerable<String> configuredTokens)
```

#### Parameters

`provided` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`configuredTokens` [IEnumerable](https://learn.microsoft.com/dotnet/api/system.collections.generic.ienumerable-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)
