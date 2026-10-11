# OperationResult&lt;T&gt;

Namespace: Vestigo.Etws

Success value or a stable error code returned by a tool or service.

```csharp
public record OperationResult<T>
```

XMLDoc2Markdown 1.8.0 stops while rendering this open generic, so this page is kept beside the generated reference.

## Properties

- `Success` — `true` when `Value` is usable.
- `Value` — the result, or the default value when `Success` is `false`.
- `ErrorCode` — stable machine-readable failure code.
- `Error` — human-readable failure detail. Token values are never included.

## Methods

- `Ok(T value)` — creates a successful result.
- `Fail(string code, string error)` — creates a failed result.
