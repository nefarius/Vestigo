using Nefarius.Utilities.ETW;
using Nefarius.Utilities.ETW.Deserializer.WPP;
using Nefarius.Utilities.ETW.Deserializer.WPP.TMF;
using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Files;
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
public sealed class SymbolCatalogService(
    VestigoOptions options,
    PathPolicy paths,
    IHttpClientFactory httpClientFactory,
    RootPolicy? roots = null)
{
    private static readonly string[] SymbolExtensions = [".pdb", ".tmf"];
    private static readonly string[] EtlExtensions = [".etl"];
    private static readonly string[] BinaryExtensions = [".sys", ".dll", ".exe"];
    private readonly RootPolicy _roots = roots ?? new RootPolicy(options);

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
            options.EffectiveSymbolCache,
            options.EffectiveInputRoots);

    public HttpClient CreateSymbolClient() => httpClientFactory.CreateClient("symbols");

    public TraceSettingsDescription GetSettings()
    {
        SymbolStoreConfiguration store = CreateStore();
        string source = "none";
        if (!string.IsNullOrWhiteSpace(options.SymbolServer))
        {
            source = "setting";
        }
        else if (!string.IsNullOrWhiteSpace(store.ServerUrl))
        {
            source = "environment";
        }

        RootDefinition? traces = _roots.Roots.FirstOrDefault(
            root => string.Equals(root.Name, PathPolicy.TracesRootName, StringComparison.OrdinalIgnoreCase));
        return new TraceSettingsDescription(
            options.EffectiveInputRoots,
            options.EffectiveOutputRoots,
            options.UsesDefaultTraceRoots,
            traces?.Name,
            traces?.Path,
            store.CacheDirectory,
            RedactUserInfo(store.ServerUrl),
            source,
            new TraceAuthDescription(options.SymbolServerAuth.Scheme.ToString(), options.SymbolServerAuth.IsConfigured),
            options.AttachableSessions,
            options.MaxInputBytes,
            options.MaxCaptureDuration,
            options.MaxEvents,
            options.MaxArchiveEntries);
    }

    public async Task<OperationResult<IReadOnlyList<BinarySymbolResolution>>> ResolveBinariesAsync(
        IReadOnlyList<string> binaryPaths,
        CancellationToken cancellationToken)
    {
        if (binaryPaths.Count == 0)
        {
            return OperationResult<IReadOnlyList<BinarySymbolResolution>>.Fail(
                "invalid_request",
                "At least one binary path is required.");
        }

        List<BinarySymbolResolution> results = [];
        List<PdbMetaData> references = [];
        List<int> referenceIndexes = [];
        for (int i = 0; i < binaryPaths.Count; i++)
        {
            PathCheck check = RequireBinary(binaryPaths[i]);
            if (!check.Success)
            {
                return OperationResult<IReadOnlyList<BinarySymbolResolution>>.Fail(check.ErrorCode!, check.Error!);
            }

            CodeViewReadResult codeView = PortableExecutableSymbols.TryReadCodeViewPdb(check.FullPath!);
            if (codeView.Metadata is null)
            {
                results.Add(new BinarySymbolResolution(
                    check.FullPath!,
                    null,
                    null,
                    PdbResolutionOrigin.Unresolved.ToString(),
                    "no_codeview",
                    codeView.Diagnostic));
                continue;
            }

            PdbMetaData metadata = codeView.Metadata.Value;
            results.Add(new BinarySymbolResolution(
                check.FullPath!,
                new PdbReferenceDescription(metadata.PdbName, metadata.Guid, metadata.Age, metadata.IndexPrefix),
                null,
                PdbResolutionOrigin.Unresolved.ToString(),
                null,
                null));
            references.Add(metadata);
            referenceIndexes.Add(results.Count - 1);
        }

        if (references.Count > 0)
        {
            IReadOnlyList<ResolvedPdb> resolved = await SymbolStore
                .ResolveAsync(references, CreateStore(), CreateSymbolClient(), cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < resolved.Count; i++)
            {
                ResolvedPdb pdb = resolved[i];
                int index = referenceIndexes[i];
                BinarySymbolResolution current = results[index];
                results[index] = current with
                {
                    PdbPath = pdb.LocalPath,
                    Origin = pdb.Origin.ToString(),
                    Failure = pdb.Origin == PdbResolutionOrigin.Unresolved
                        ? MapResolutionFailure(pdb)
                        : null,
                    Detail = pdb.Detail
                };
            }
        }

        return OperationResult<IReadOnlyList<BinarySymbolResolution>>.Ok(results);
    }

    public async Task<OperationResult<IReadOnlyList<string>>> ResolveBinaryPdbPathsAsync(
        IReadOnlyList<string> binaryPaths,
        CancellationToken cancellationToken)
    {
        if (binaryPaths.Count == 0)
        {
            return OperationResult<IReadOnlyList<string>>.Ok([]);
        }

        OperationResult<IReadOnlyList<BinarySymbolResolution>> resolved =
            await ResolveBinariesAsync(binaryPaths, cancellationToken).ConfigureAwait(false);
        if (!resolved.Success)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(resolved.ErrorCode!, resolved.Error!);
        }

        List<string> pdbPaths = [];
        foreach (BinarySymbolResolution item in resolved.Value!)
        {
            if (item.Failure == "symbol_auth_failed")
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    "symbol_auth_failed",
                    item.Detail ?? "The symbol server rejected the request.");
            }

            if (item.Failure == "no_codeview")
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    "no_codeview",
                    item.Detail ?? $"'{item.BinaryPath}' has no CodeView PDB identity.");
            }

            if (string.IsNullOrEmpty(item.PdbPath))
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    "symbols_unresolved",
                    item.Detail ?? $"Could not resolve a PDB for '{item.BinaryPath}'.");
            }

            PathCheck check = paths.RequireExistingFile(item.PdbPath, [".pdb"]);
            if (!check.Success)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(check.ErrorCode!, check.Error!);
            }

            pdbPaths.Add(check.FullPath!);
        }

        return OperationResult<IReadOnlyList<string>>.Ok(pdbPaths);
    }

    internal static bool IsUnauthorized(PdbResolutionFailure failure, string? detail) =>
        failure == PdbResolutionFailure.Unauthorized ||
        (failure == PdbResolutionFailure.DownloadFailed && LooksLikeAuthStatus(detail));

    internal static bool LooksLikeAuthStatus(string? detail) =>
        detail is not null &&
        (ContainsStatusCodeToken(detail, "401") ||
         ContainsStatusCodeToken(detail, "403") ||
         detail.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsStatusCodeToken(string detail, string code)
    {
        int start = 0;
        while (start <= detail.Length - code.Length)
        {
            int index = detail.IndexOf(code, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            bool leftBoundary = index == 0 || !IsStatusCodeTokenChar(detail[index - 1]);
            int after = index + code.Length;
            bool rightBoundary = after == detail.Length || !IsStatusCodeTokenChar(detail[after]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    private static bool IsStatusCodeTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    internal static string? RedactUserInfo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return url;
        }

        return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.GetLeftPart(UriPartial.Path);
    }

    private PathCheck RequireBinary(string requestedPath)
    {
        PathCheck check = _roots.ResolveExisting(requestedPath, RootAccess.Read);
        if (!check.Success)
        {
            return check;
        }

        if (!PathPolicy.HasExtension(check.FullPath!, BinaryExtensions))
        {
            return PathCheck.Fail(
                "unsupported_extension",
                $"'{Path.GetFileName(requestedPath)}' uses an unsupported extension.");
        }

        return FileAccessGuard.Validate(check);
    }

    private static string MapResolutionFailure(ResolvedPdb pdb)
    {
        if (IsUnauthorized(pdb.Failure, pdb.Detail))
        {
            return "symbol_auth_failed";
        }

        return pdb.Failure == PdbResolutionFailure.NotFoundOnServer ? "not_found" : "symbols_unresolved";
    }
}
