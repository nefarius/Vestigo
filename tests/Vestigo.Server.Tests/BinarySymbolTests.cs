using System.Text;

using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class BinarySymbolTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-binary").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public BinarySymbolTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.TraceDirectory);
        Directory.CreateDirectory(VestigoPaths.DefaultSymbolCache);
    }

    [Fact]
    public async Task ResolveBinaries_FindsPdbUnderTheTraceFolder()
    {
        string fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "BthPS3PSM.pdb");
        Assert.True(PdbFileIdentityReader.TryRead(fixture, out PdbFileIdentity identity));
        File.Copy(fixture, Path.Combine(VestigoPaths.TraceDirectory, "BthPS3PSM.pdb"), overwrite: true);

        string folder = Directory.CreateTempSubdirectory("vestigo-bins").FullName;
        string binary = Path.Combine(folder, "BthPS3PSM.sys");
        File.WriteAllBytes(binary, MinimalPe.WithCodeView("BthPS3PSM.pdb", identity.Guid, identity.Age));

        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "bins", Path = folder, Access = RootAccess.Read }]
        };
        SymbolCatalogService symbols = new(
            options,
            new PathPolicy(options),
            new UnusedHttp(),
            new RootPolicy(options));

        OperationResult<IReadOnlyList<BinarySymbolResolution>> resolved =
            await symbols.ResolveBinariesAsync([binary], CancellationToken.None);

        Assert.True(resolved.Success, resolved.Error);
        BinarySymbolResolution item = Assert.Single(resolved.Value!);
        Assert.Equal("LocalSearch", item.Origin);
        Assert.NotNull(item.PdbPath);
        Assert.Null(item.Failure);
    }

    [Fact]
    public async Task ResolveBinaries_ReportsNoCodeViewAndOutsideRoot()
    {
        string folder = Directory.CreateTempSubdirectory("vestigo-bins").FullName;
        string stripped = Path.Combine(folder, "stripped.dll");
        File.WriteAllBytes(stripped, MinimalPe.WithoutCodeView());
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "bins", Path = folder, Access = RootAccess.Read }]
        };
        SymbolCatalogService symbols = new(options, new PathPolicy(options), new UnusedHttp(), new RootPolicy(options));

        OperationResult<IReadOnlyList<BinarySymbolResolution>> missing =
            await symbols.ResolveBinariesAsync([stripped], CancellationToken.None);
        OperationResult<IReadOnlyList<BinarySymbolResolution>> outside =
            await symbols.ResolveBinariesAsync([@"C:\Windows\System32\ntdll.dll"], CancellationToken.None);

        Assert.True(missing.Success);
        Assert.Equal("no_codeview", Assert.Single(missing.Value!).Failure);
        Assert.False(outside.Success);
        Assert.True(
            outside.ErrorCode is "outside_root" or "roots_not_configured" or "access_denied",
            outside.ErrorCode);
    }

    [Fact]
    public async Task ResolveBinaryPdbPaths_AcceptsPdbUnderStoreCacheDirectory()
    {
        string envCache = Directory.CreateTempSubdirectory("vestigo-env-cache").FullName;
        string? previous = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "cache*" + envCache);

            string fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "BthPS3PSM.pdb");
            Assert.True(PdbFileIdentityReader.TryRead(fixture, out PdbFileIdentity identity));

            string folder = Directory.CreateTempSubdirectory("vestigo-bins").FullName;
            string binary = Path.Combine(folder, "BthPS3PSM.sys");
            File.WriteAllBytes(binary, MinimalPe.WithCodeView("BthPS3PSM.pdb", identity.Guid, identity.Age));

            VestigoOptions options = new()
            {
                Roots = [new RootDefinition { Name = "bins", Path = folder, Access = RootAccess.Read }]
            };
            SymbolCatalogService symbols = new(
                options,
                new PathPolicy(options),
                new UnusedHttp(),
                new RootPolicy(options));

            CodeViewReadResult codeView = PortableExecutableSymbols.TryReadCodeViewPdb(binary);
            Assert.NotNull(codeView.Metadata);
            string cached = Path.Combine(
                envCache,
                codeView.Metadata.Value.IndexPrefix.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.Copy(fixture, cached, overwrite: true);

            Assert.Equal(Path.GetFullPath(envCache), Path.GetFullPath(symbols.CreateStore().CacheDirectory));

            OperationResult<IReadOnlyList<string>> resolved =
                await symbols.ResolveBinaryPdbPathsAsync([binary], CancellationToken.None);

            Assert.True(resolved.Success, resolved.Error);
            string pdb = Assert.Single(resolved.Value!);
            Assert.True(
                pdb.StartsWith(Path.GetFullPath(envCache), StringComparison.OrdinalIgnoreCase),
                pdb);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", previous);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class UnusedHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

internal static class MinimalPe
{
    public static byte[] WithoutCodeView()
    {
        byte[] pe = new byte[0x400];
        WritePe32Plus(pe, debugRva: 0, debugSize: 0);
        return pe;
    }

    public static byte[] WithCodeView(string pdbName, Guid guid, int age)
    {
        byte[] path = Encoding.UTF8.GetBytes(pdbName);
        int rsdsSize = 4 + 16 + 4 + path.Length + 1;
        int debugDirSize = 28;
        byte[] pe = new byte[0x400];
        const int sectionFile = 0x200;
        const int sectionRva = 0x1000;
        WritePe32Plus(pe, sectionRva, debugDirSize);

        // IMAGE_DEBUG_DIRECTORY
        BitConverter.GetBytes(2).CopyTo(pe, sectionFile + 12); // Type = CodeView
        BitConverter.GetBytes(rsdsSize).CopyTo(pe, sectionFile + 16);
        BitConverter.GetBytes(sectionRva + debugDirSize).CopyTo(pe, sectionFile + 20);
        BitConverter.GetBytes(sectionFile + debugDirSize).CopyTo(pe, sectionFile + 24);

        int rsds = sectionFile + debugDirSize;
        Encoding.ASCII.GetBytes("RSDS").CopyTo(pe, rsds);
        guid.ToByteArray().CopyTo(pe, rsds + 4);
        BitConverter.GetBytes(age).CopyTo(pe, rsds + 20);
        path.CopyTo(pe, rsds + 24);
        return pe;
    }

    private static void WritePe32Plus(byte[] pe, int debugRva, int debugSize)
    {
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(pe, 0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84); // Machine
        BitConverter.GetBytes((ushort)1).CopyTo(pe, 0x86); // NumberOfSections
        BitConverter.GetBytes((ushort)0xF0).CopyTo(pe, 0x94); // SizeOfOptionalHeader
        BitConverter.GetBytes((ushort)0x0022).CopyTo(pe, 0x96); // Characteristics
        const int optional = 0x98;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(pe, optional); // PE32+
        BitConverter.GetBytes(0x1000).CopyTo(pe, optional + 32); // SectionAlignment
        BitConverter.GetBytes(0x200).CopyTo(pe, optional + 36); // FileAlignment
        BitConverter.GetBytes(0x2000).CopyTo(pe, optional + 56); // SizeOfImage
        BitConverter.GetBytes(0x200).CopyTo(pe, optional + 60); // SizeOfHeaders
        BitConverter.GetBytes((ushort)3).CopyTo(pe, optional + 68); // Subsystem
        BitConverter.GetBytes(16).CopyTo(pe, optional + 108); // NumberOfRvaAndSizes
        BitConverter.GetBytes(debugRva).CopyTo(pe, optional + 112 + (6 * 8));
        BitConverter.GetBytes(debugSize).CopyTo(pe, optional + 112 + (6 * 8) + 4);

        int section = optional + 0xF0;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(pe, section);
        BitConverter.GetBytes(0x200).CopyTo(pe, section + 8);
        BitConverter.GetBytes(0x1000).CopyTo(pe, section + 12);
        BitConverter.GetBytes(0x200).CopyTo(pe, section + 16);
        BitConverter.GetBytes(0x200).CopyTo(pe, section + 20);
        BitConverter.GetBytes(unchecked((int)0x40000040)).CopyTo(pe, section + 36);
    }
}
