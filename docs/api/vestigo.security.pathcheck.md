# PathCheck

Namespace: Vestigo.Security

Outcome of a path-policy check.

```csharp
public record PathCheck
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [PathCheck](./vestigo.security.pathcheck.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Properties

### <a id="properties-error"/>**Error**

```csharp
public string Error { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-errorcode"/>**ErrorCode**

```csharp
public string ErrorCode { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-fullpath"/>**FullPath**

```csharp
public string FullPath { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-success"/>**Success**

```csharp
public bool Success { get; set; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

## Constructors

### <a id="constructors-.ctor"/>**PathCheck(Boolean, String, String, String)**

Outcome of a path-policy check.

```csharp
public PathCheck(bool Success, string FullPath, string ErrorCode, string Error)
```

#### Parameters

`Success` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`FullPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`ErrorCode` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Error` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-equals"/>**Equals(PathCheck)**

```csharp
public bool Equals(PathCheck other)
```

#### Parameters

`other` [PathCheck](./vestigo.security.pathcheck.md)<br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)

### <a id="methods-fail"/>**Fail(String, String)**

```csharp
public static PathCheck Fail(string code, string error)
```

#### Parameters

`code` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`error` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[PathCheck](./vestigo.security.pathcheck.md)

### <a id="methods-ok"/>**Ok(String)**

```csharp
public static PathCheck Ok(string fullPath)
```

#### Parameters

`fullPath` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[PathCheck](./vestigo.security.pathcheck.md)
