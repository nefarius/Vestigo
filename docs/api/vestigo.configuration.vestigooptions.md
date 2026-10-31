# VestigoOptions

Namespace: Vestigo.Configuration

Host, authorization, path, and capture limits loaded from configuration.

```csharp
public sealed class VestigoOptions
```

Inheritance [Object](https://learn.microsoft.com/dotnet/api/system.object) → [VestigoOptions](./vestigo.configuration.vestigooptions.md)<br>
Attributes [NullableContextAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullablecontextattribute), [NullableAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.nullableattribute)

## Fields

### <a id="fields-sectionname"/>**SectionName**

```csharp
public static string SectionName;
```

## Properties

### <a id="properties-allowedinputroots"/>**AllowedInputRoots**

```csharp
public List<String> AllowedInputRoots { get; set; }
```

#### Property Value

[List](https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-allowedoutputroots"/>**AllowedOutputRoots**

```csharp
public List<String> AllowedOutputRoots { get; set; }
```

#### Property Value

[List](https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-apitokens"/>**ApiTokens**

```csharp
public List<String> ApiTokens { get; set; }
```

#### Property Value

[List](https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1)<[String](https://learn.microsoft.com/dotnet/api/system.string)><br>

### <a id="properties-authenticationenabled"/>**AuthenticationEnabled**

```csharp
public bool AuthenticationEnabled { get; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

### <a id="properties-buffersizekb"/>**BufferSizeKb**

```csharp
public uint BufferSizeKb { get; set; }
```

#### Property Value

[UInt32](https://learn.microsoft.com/dotnet/api/system.uint32)<br>

### <a id="properties-clockresolution"/>**ClockResolution**

```csharp
public string ClockResolution { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-flushtimerseconds"/>**FlushTimerSeconds**

```csharp
public uint FlushTimerSeconds { get; set; }
```

#### Property Value

[UInt32](https://learn.microsoft.com/dotnet/api/system.uint32)<br>

### <a id="properties-isboundbeyondloopback"/>**IsBoundBeyondLoopback**

True when [VestigoOptions.ListenAddress](./vestigo.configuration.vestigooptions.md#listenaddress) is an IP address other than loopback, such as 0.0.0.0 or ::.

```csharp
public bool IsBoundBeyondLoopback { get; }
```

#### Property Value

[Boolean](https://learn.microsoft.com/dotnet/api/system.boolean)<br>

### <a id="properties-listenaddress"/>**ListenAddress**

```csharp
public string ListenAddress { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-listenport"/>**ListenPort**

```csharp
public int ListenPort { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-maxcaptureduration"/>**MaxCaptureDuration**

```csharp
public TimeSpan MaxCaptureDuration { get; set; }
```

#### Property Value

[TimeSpan](https://learn.microsoft.com/dotnet/api/system.timespan)<br>

### <a id="properties-maxconcurrentcaptures"/>**MaxConcurrentCaptures**

```csharp
public int MaxConcurrentCaptures { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-maxevents"/>**MaxEvents**

```csharp
public int MaxEvents { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-maxinputbytes"/>**MaxInputBytes**

```csharp
public long MaxInputBytes { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

### <a id="properties-maxoutputbytes"/>**MaxOutputBytes**

```csharp
public long MaxOutputBytes { get; set; }
```

#### Property Value

[Int64](https://learn.microsoft.com/dotnet/api/system.int64)<br>

### <a id="properties-maxreadbatch"/>**MaxReadBatch**

```csharp
public int MaxReadBatch { get; set; }
```

#### Property Value

[Int32](https://learn.microsoft.com/dotnet/api/system.int32)<br>

### <a id="properties-spoolretention"/>**SpoolRetention**

```csharp
public TimeSpan SpoolRetention { get; set; }
```

#### Property Value

[TimeSpan](https://learn.microsoft.com/dotnet/api/system.timespan)<br>

### <a id="properties-symbolcache"/>**SymbolCache**

```csharp
public string SymbolCache { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

### <a id="properties-symbolserver"/>**SymbolServer**

```csharp
public string SymbolServer { get; set; }
```

#### Property Value

[String](https://learn.microsoft.com/dotnet/api/system.string)<br>

## Constructors

### <a id="constructors-.ctor"/>**VestigoOptions()**

```csharp
public VestigoOptions()
```

## Methods

### <a id="methods-getexposurewarning"/>**GetExposureWarning()**

Returns a startup warning when the server is reachable from the network without any API token, otherwise null.
 The message never contains token values.

```csharp
public string GetExposureWarning()
```

#### Returns

[String](https://learn.microsoft.com/dotnet/api/system.string)

### <a id="methods-validate"/>**Validate()**

```csharp
public void Validate()
```
