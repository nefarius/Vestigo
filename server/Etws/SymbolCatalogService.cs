using Nefarius.Utilities.ETW;
using Nefarius.Utilities.ETW.Deserializer.WPP;
using Nefarius.Utilities.ETW.Deserializer.WPP.TMF;
using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>One WPP control GUID declared by a PDB.</summary>
public sealed record ProviderDescription(Guid ControlGuid, string Name, IReadOnlyList<string> BitFlags, string? SymbolFile);

/// <summary>WPP controls found in one PDB.</summary>
public sealed record PdbInspection(string Path, IReadOnlyList<ProviderDescription> Providers);

/// <summary>A PDB identity referenced by an ETL file.</summary>
public sealed record PdbReferenceDescription(string PdbName, Guid Guid, int Age, string IndexPrefix);

/// <summary>Result of loading explicit PDB and TMF decoding sources.</summary>
public sealed record SymbolLoadDescription(int SourceCount, bool CanRewriteProviderNames, IReadOnlyList<string> Diagnostics);

/// <summary>
///     Inspects PDBs, loads decoding sources, and builds the configured symbol store.
/// </summary>
public sealed class SymbolCatalogService(VestigoOptions options, PathPolicy paths, IHttpClientFactory httpClientFactory)
{
    private static readonly string[] SymbolExtensions = [".pdb", ".tmf"];
    private static readonly string[] EtlExtensions = [".etl"];

    public OperationResult<PdbInspection> InspectPdb(string pdbPath)
    {
        PathCheck check = paths.RequireExistingFile(pdbPath, [".pdb"]);
        if (!check.Success)
        {
            return OperationResult<PdbInspection>.Fail(check.ErrorCode!, check.Error!);
        }

        try
        {
            IReadOnlyCollection<WppTraceControl> controls =
                PdbFileDecodingContextType.EnumerateTraceControls(check.FullPath!);
            PdbInspection inspection = new(
                check.FullPath!,
                controls.Select(control => new ProviderDescription(
                    control.ControlGuid,
                    control.Name,
                    control.BitFlags,
                    control.OriginalSymbolFileName)).ToArray());
            return OperationResult<PdbInspection>.Ok(inspection);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<PdbInspection>.Fail("invalid_pdb", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public OperationResult<SymbolLoadDescription> ValidateSources(IReadOnlyList<string> symbolPaths)
    {
        OperationResult<DecodingSourceSet> loaded = LoadExplicit(symbolPaths);
        if (!loaded.Success)
        {
            return OperationResult<SymbolLoadDescription>.Fail(loaded.ErrorCode!, loaded.Error!);
        }

        DecodingSourceSet set = loaded.Value!;
        return OperationResult<SymbolLoadDescription>.Ok(new SymbolLoadDescription(
            set.Sources.Count,
            set.CanRewriteProviderNames,
            set.Diagnostics.Take(50).ToArray()));
    }

    public OperationResult<IReadOnlyList<PdbReferenceDescription>> ScanEtl(IReadOnlyList<string> etlPaths)
    {
        IReadOnlyList<PathCheck> checks = paths.RequireExistingFiles(etlPaths, EtlExtensions);
        PathCheck? failed = checks.FirstOrDefault(check => !check.Success);
        if (failed is not null)
        {
            return OperationResult<IReadOnlyList<PdbReferenceDescription>>.Fail(failed.ErrorCode!, failed.Error!);
        }

        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<IReadOnlyList<PdbReferenceDescription>>.Fail(
                "windows_required",
                "ETL scanning requires Windows ETW.");
        }

        try
        {
            IReadOnlyCollection<PdbMetaData> references =
                EtwUtil.EnumeratePdbReferences(checks.Select(check => check.FullPath!));
            IReadOnlyList<PdbReferenceDescription> described = references
                .Select(reference => new PdbReferenceDescription(
                    reference.PdbName,
                    reference.Guid,
                    reference.Age,
                    reference.IndexPrefix))
                .ToArray();
            return OperationResult<IReadOnlyList<PdbReferenceDescription>>.Ok(described);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<IReadOnlyList<PdbReferenceDescription>>.Fail(
                "scan_failed",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public OperationResult<DecodingSourceSet> LoadExplicit(IReadOnlyList<string> symbolPaths)
    {
        if (symbolPaths.Count == 0)
        {
            return OperationResult<DecodingSourceSet>.Ok(new DecodingSourceSet(null, [], []));
        }

        IReadOnlyList<PathCheck> checks = paths.RequireExistingFiles(symbolPaths, SymbolExtensions);
        PathCheck? failed = checks.FirstOrDefault(check => !check.Success);
        if (failed is not null)
        {
            return OperationResult<DecodingSourceSet>.Fail(failed.ErrorCode!, failed.Error!);
        }

        DecodingSourceSet loaded = DecodingSources.Load(checks.Select(check => check.FullPath!));
        if (loaded.Context is null)
        {
            string detail = loaded.Diagnostics.LastOrDefault() ?? "No decoding sources were loaded.";
            return OperationResult<DecodingSourceSet>.Fail("symbols_not_loaded", detail);
        }

        return OperationResult<DecodingSourceSet>.Ok(loaded);
    }

    public SymbolStoreConfiguration CreateStore() =>
        SymbolStoreConfiguration.Resolve(
            options.SymbolServer,
            options.SymbolCache,
            Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"),
            options.SymbolCache ?? VestigoPaths.DefaultSymbolCache,
            options.AllowedInputRoots);

    public HttpClient CreateSymbolClient() => httpClientFactory.CreateClient("symbols");
}
