using System.Text;

using Nefarius.Utilities.ETW;
using Nefarius.Utilities.ETW.Deserializer.WPP;
using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>Summary of one bounded ETL conversion. Event text stays in the output file.</summary>
public sealed record ConversionSummary(
    string OutputPath,
    string Format,
    int EventCount,
    long OutputBytes,
    int MissingFormatCount,
    bool Truncated,
    int ResolvedSymbolCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Converts ETL files to JSON or NDJSON inside the configured output roots.</summary>
public sealed class OfflineConversionService(
    VestigoOptions options,
    PathPolicy paths,
    SymbolCatalogService symbols)
{
    public async Task<OperationResult<ConversionSummary>> ConvertAsync(
        IReadOnlyList<string> etlPaths,
        string outputPath,
        string format,
        IReadOnlyList<string> symbolPaths,
        bool resolveSymbols,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (etlPaths.Count == 0)
        {
            return OperationResult<ConversionSummary>.Fail("invalid_request", "At least one ETL path is required.");
        }

        string normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("json" or "ndjson"))
        {
            return OperationResult<ConversionSummary>.Fail("invalid_request", "Format must be json or ndjson.");
        }

        IReadOnlyList<PathCheck> inputs = paths.RequireExistingFiles(etlPaths, [".etl"]);
        PathCheck? failedInput = inputs.FirstOrDefault(check => !check.Success);
        if (failedInput is not null)
        {
            return OperationResult<ConversionSummary>.Fail(failedInput.ErrorCode!, failedInput.Error!);
        }

        string extension = normalizedFormat == "json" ? ".json" : ".ndjson";
        PathCheck output = paths.RequireOutputFile(outputPath, [extension], overwrite);
        if (!output.Success)
        {
            return OperationResult<ConversionSummary>.Fail(output.ErrorCode!, output.Error!);
        }

        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<ConversionSummary>.Fail("windows_required", "ETL conversion requires Windows ETW.");
        }

        List<DecodingContextType> sources = [];
        List<string> diagnostics = [];
        OperationResult<DecodingSourceSet> explicitSources = symbols.LoadExplicit(symbolPaths);
        if (!explicitSources.Success)
        {
            return OperationResult<ConversionSummary>.Fail(explicitSources.ErrorCode!, explicitSources.Error!);
        }

        if (explicitSources.Value!.Sources.Count > 0)
        {
            sources.AddRange(explicitSources.Value.Sources);
            diagnostics.AddRange(explicitSources.Value.Diagnostics.Take(50));
        }

        if (resolveSymbols)
        {
            TraceSymbolResolution resolved = await DecodingSources.ResolveTraceSymbolsAsync(
                inputs.Select(check => check.FullPath!),
                symbols.CreateStore(),
                symbols.CreateSymbolClient(),
                cancellationToken);
            sources.AddRange(resolved.Sources);
            diagnostics.AddRange(resolved.Diagnostics.Take(50));
        }

        DecodingContext? context = sources.Count == 0 ? null : new DecodingContext(sources);
        bool rewrite = sources.OfType<PdbFileDecodingContextType>()
            .Any(source => source.WppTraceControls.Any(control => !string.IsNullOrWhiteSpace(control.Name)));
        int missing = 0;
        int events = 0;
        long bytes = 0;
        bool truncated = false;
        string finalPath = output.FullPath!;
        string? directory = Path.GetDirectoryName(finalPath);
        string tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool published = false;
        try
        {
            await using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                bool firstJson = true;
                if (normalizedFormat == "json")
                {
                    await writer.WriteAsync("[");
                    bytes++;
                }

                try
                {
                    await foreach (ReadOnlyMemory<byte> item in EtwUtil.EnumerateEventsAsync(
                                       inputs.Select(check => check.FullPath!),
                                       converter =>
                                       {
                                           converter.WppDecodingContext = context;
                                           converter.RewriteWppProviderName = rewrite;
                                           converter.OnWppFormatMissing = (_, _, _) => Interlocked.Increment(ref missing);
                                       },
                                       cancellationToken))
                    {
                        long overhead = normalizedFormat == "json"
                            ? (firstJson ? 0 : 1) + 1
                            : 1;
                        if (events >= options.MaxEvents || bytes + item.Length + overhead > options.MaxOutputBytes)
                        {
                            truncated = true;
                            break;
                        }

                        if (normalizedFormat == "json")
                        {
                            if (!firstJson)
                            {
                                await writer.WriteAsync(",");
                                bytes++;
                            }

                            firstJson = false;
                        }

                        string text = Encoding.UTF8.GetString(item.Span);
                        await writer.WriteAsync(text);
                        bytes += item.Length;
                        if (normalizedFormat == "ndjson")
                        {
                            await writer.WriteAsync('\n');
                            bytes++;
                        }

                        events++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    return OperationResult<ConversionSummary>.Fail(
                        "convert_failed",
                        $"{ex.GetType().Name}: {ex.Message}");
                }

                if (normalizedFormat == "json")
                {
                    await writer.WriteAsync("]");
                    bytes++;
                }

                await writer.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            published = true;
            return OperationResult<ConversionSummary>.Ok(new ConversionSummary(
                finalPath,
                normalizedFormat,
                events,
                bytes,
                missing,
                truncated,
                sources.Count,
                diagnostics));
        }
        finally
        {
            if (!published)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
