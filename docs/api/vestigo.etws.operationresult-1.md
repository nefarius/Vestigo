# OperationResult&lt;T&gt;

Namespace: Vestigo.Etws

Success value or a stable error code returned by a tool or service.

```csharp
public record OperationResult<T>
```

#### Type Parameters

`T`<br>

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [OperationResult](./vestigo.etws.operationresult-1.md)<T><br>
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

### <a id="properties-success"/>**Success**

```csharp
public bool Success { get; set; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

### <a id="properties-value"/>**Value**

```csharp
public T Value { get; set; }
```

#### Property Value

T<br>

## Constructors

### <a id="constructors-.ctor"/>**OperationResult(Boolean, T, String, String)**

Success value or a stable error code returned by a tool or service.

```csharp
public OperationResult(bool Success, T Value, string ErrorCode, string Error)
```

#### Parameters

`Success` [Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

`Value` T<br>

`ErrorCode` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`Error` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Methods

### <a id="methods-equals"/>**Equals(OperationResult&lt;T&gt;)**

```csharp
public bool Equals(OperationResult<T> other)
```

#### Parameters

`other` [OperationResult](./vestigo.etws.operationresult-1.md)<T><br>

#### Returns

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)

### <a id="methods-fail"/>**Fail(String, String)**

```csharp
public static OperationResult<T> Fail(string code, string error)
```

#### Parameters

`code` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

`error` [String](https://learn.microsoft.com/dotnet/api/system.string)<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<T>

### <a id="methods-ok"/>**Ok(T)**

```csharp
public static OperationResult<T> Ok(T value)
```

#### Parameters

`value` T<br>

#### Returns

[OperationResult](./vestigo.etws.operationresult-1.md)<T>
