using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Installer.Actions.Logic;
using Vestigo.Modules;
using Vestigo.Security;
using Vestigo.Security.Signatures;

namespace Vestigo.Server.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("vestigo-diagnostics").FullName;

    public void Dispose()
    {
        // Directory.Delete(recursive) returns ERROR_INVALID_PARAMETER on a directory junction.
        RemoveDirectoryLinks(_parent);
        Directory.Delete(_parent, recursive: true);
    }

    [Fact]
    public void Inspect_ReportsArchitectureCharacteristicsAndPdbFileName()
    {
        AssertImage(0x014C, hybrid: false, "x86");
        AssertImage(0x8664, hybrid: false, "x64");
        AssertImage(0x8664, hybrid: true, "arm64ec");
        AssertImage(0xAA64, hybrid: false, "arm64");
        AssertImage(0xAA64, hybrid: true, "arm64x");
        AssertImage(0x01C4, hybrid: false, "armnt");

        Guid guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] withPdb = MinimalPe.WithCodeView(@"C:\symbols\nested\vestigo.pdb", guid, 7);
        BinaryInspection pdb = Inspect(withPdb);
        Assert.Equal("vestigo.pdb", pdb.PdbName);
        Assert.Equal(guid.ToString("D"), pdb.PdbGuid);
        Assert.Equal(7, pdb.PdbAge);
        Assert.DoesNotContain("symbols", pdb.PdbName, StringComparison.OrdinalIgnoreCase);

        BinaryInspection hashed = Inspect(withPdb, computeSha256: true);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(withPdb)), hashed.Sha256);
    }

    [Fact]
    public void Inspect_RejectsNonPeMalformedAndOversizedInput()
    {
        string root = Directory.CreateDirectory(Path.Combine(_parent, "files")).FullName;
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello");
        File.WriteAllBytes(Path.Combine(root, "broken.dll"), [(byte)'M', (byte)'Z', 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "ok.dll"), Image(0x8664, hybrid: false));
        BinaryTools tools = Tools(root, maxInspectBytes: 64);

        Assert.Equal("not_pe", tools.InspectBinary(Path.Combine(root, "note.txt")).ErrorCode);
        Assert.Equal("malformed_pe", tools.InspectBinary(Path.Combine(root, "broken.dll")).ErrorCode);
        Assert.Equal("file_too_large", tools.InspectBinary(Path.Combine(root, "ok.dll")).ErrorCode);
        Assert.Equal("not_a_file", tools.InspectBinary(root).ErrorCode);
    }

    [Fact]
    public void Inspect_ReadsVersionWhenTheResourceFits()
    {
        BinaryInspection image = Inspect(MinimalPe.WithVersion());

        Assert.Equal("1.2.3.4", image.FileVersion);
        Assert.Equal("5.6.7.8", image.ProductVersion);
    }

    [Fact]
    public void Inspect_SkipsVersionWhenTheResourceExtendsPastTheFile()
    {
        BinaryInspection image = Inspect(MinimalPe.WithVersionPastEnd());

        Assert.Null(image.FileVersion);
        Assert.Null(image.ProductVersion);
    }

    [Fact]
    public void Inspect_SkipsVersionWhenFixedInfoExtendsPastTheResource()
    {
        BinaryInspection image = Inspect(MinimalPe.WithVersionKeyPastFixedInfo());

        Assert.Null(image.FileVersion);
        Assert.Null(image.ProductVersion);
    }

    [Fact]
    public void SignatureDecision_KeepsUnavailableDistinctFromUnsigned()
    {
        AssertOutcome(0, null, "valid", "embedded");
        AssertOutcome(SignatureOutcomeMap.TrustENoSignature, new CatalogLookup(CatalogLookupKind.NotFound, 0), "unsigned", "none");
        AssertOutcome(SignatureOutcomeMap.TrustENoSignature, new CatalogLookup(CatalogLookupKind.Unavailable, SignatureOutcomeMap.TrustEProviderUnknown), "unavailable", "none");
        AssertOutcome(SignatureOutcomeMap.TrustENoSignature, null, "unavailable", "none");
        AssertOutcome(SignatureOutcomeMap.CertEUntrustedRoot, null, "untrusted", "embedded");
        AssertOutcome(SignatureOutcomeMap.TrustEBadDigest, null, "invalid", "embedded");
        AssertOutcome(SignatureOutcomeMap.CertERevoked, null, "invalid", "embedded");
        AssertOutcome(SignatureOutcomeMap.CryptERevocationOffline, null, "unavailable", "embedded");
        AssertOutcome(
            SignatureOutcomeMap.TrustENoSignature,
            new CatalogLookup(CatalogLookupKind.Found, 0),
            "valid",
            "catalog");
        AssertOutcome(
            SignatureOutcomeMap.TrustESubjectFormUnknown,
            new CatalogLookup(CatalogLookupKind.Found, SignatureOutcomeMap.CertEChaining),
            "untrusted",
            "catalog");
        AssertOutcome(
            SignatureOutcomeMap.TrustEProviderUnknown,
            new CatalogLookup(CatalogLookupKind.Found, SignatureOutcomeMap.CertERevocationFailure),
            "unavailable",
            "catalog");
        Assert.NotEqual(
            SignatureOutcomeMap.Unsigned,
            SignatureOutcomeMap.Map(SignatureOutcomeMap.CryptENoRevocationCheck, signaturePresent: false));
    }

    [Fact]
    public void VerifyFileSignature_RefusesOnlineRevocationUntilEnabled()
    {
        string root = Directory.CreateDirectory(Path.Combine(_parent, "signed")).FullName;
        string file = Path.Combine(root, "a.dll");
        File.WriteAllBytes(file, Image(0x8664, hybrid: false));
        CountingVerifier verifier = new();
        BinaryTools blocked = Tools(root, verifier: verifier);

        OperationResult<SignatureVerification> refused = blocked.VerifyFileSignature(file, revocation: "online");
        OperationResult<SignatureVerification> bad = blocked.VerifyFileSignature(file, revocation: "live");

        Assert.Equal("revocation_not_allowed", refused.ErrorCode);
        Assert.Equal("invalid_request", bad.ErrorCode);
        Assert.Equal(0, verifier.Calls);

        BinaryTools allowed = Tools(root, verifier: verifier, allowOnline: true);
        OperationResult<SignatureVerification> online = allowed.VerifyFileSignature(file, revocation: "online", includeSigner: true);

        Assert.True(online.Success, online.Error);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(RevocationMode.Online, verifier.Mode);
        Assert.True(verifier.IncludeSigner);
        Assert.Equal(file, verifier.Path);
    }

    [Fact]
    public void ListProcessModules_AliasesInsideRootsAndOmitsOutsidePaths()
    {
        string root = Directory.CreateDirectory(Path.Combine(_parent, "mods")).FullName;
        string inside = Path.Combine(root, "local.dll");
        File.WriteAllBytes(inside, [1]);
        string secret = Path.Combine(_parent, "secret.dll");
        File.WriteAllBytes(secret, [2]);
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        FakeModules source = new(ProcessModuleQuery.Ok(start, partial: true, truncated: false, skipped: 1,
        [
            new LoadedModuleRecord("local.dll", 0x1000, 4096, "x64", "1.0.0.0", "1.0.0.0", inside),
            new LoadedModuleRecord("secret.dll", null, 8, null, null, null, secret)
        ]));
        RecordingLogger logger = new();
        ProcessModuleTools tools = Modules(root, source, logger, maxModules: 10);

        OperationResult<ProcessModuleList> hidden = tools.ListProcessModules(40, includePaths: false, expectedStartTime: start);
        OperationResult<ProcessModuleList> shown = tools.ListProcessModules(40, includePaths: true, expectedStartTime: start.AddMilliseconds(400));

        string hiddenJson = JsonSerializer.Serialize(hidden.Value);
        Assert.DoesNotContain(secret, hiddenJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(inside, hiddenJson, StringComparison.OrdinalIgnoreCase);
        Assert.All(hidden.Value!.Modules, module => Assert.Null(module.Path));
        Assert.False(hidden.Value.Modules[1].PathOutsideRoots);

        ProcessModuleInfo local = shown.Value!.Modules[0];
        ProcessModuleInfo outside = shown.Value.Modules[1];
        Assert.Equal("work:" + Path.GetFileName(inside), local.Path);
        Assert.False(local.PathOutsideRoots);
        Assert.Equal("0x1000", local.BaseAddress);
        Assert.Null(outside.Path);
        Assert.True(outside.PathOutsideRoots);
        Assert.Null(outside.BaseAddress);
        string shownJson = JsonSerializer.Serialize(shown.Value);
        Assert.DoesNotContain(secret, shownJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(inside, shownJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, string.Join('\n', logger.Lines), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial True", string.Join('\n', logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ListProcessModules_FollowsLinksBeforeAliasing()
    {
        string root = Directory.CreateDirectory(Path.Combine(_parent, "linked")).FullName;
        string outsideDir = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;
        string secret = Path.Combine(outsideDir, "hidden.dll");
        File.WriteAllBytes(secret, [3]);
        string link = Path.Combine(root, "escape.dll");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic link privilege is not available on this machine.");
        }

        ProcessModuleTools tools = Modules(root, new FakeModules(ProcessModuleQuery.Ok(null, false, false, 0,
        [
            new LoadedModuleRecord("escape.dll", 1, 1, "x64", null, null, link)
        ])));
        OperationResult<ProcessModuleList> result = tools.ListProcessModules(7, includePaths: true);

        Assert.Null(result.Value!.Modules[0].Path);
        Assert.True(result.Value.Modules[0].PathOutsideRoots);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListProcessModules_FollowsJunctionsBeforeAliasing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }

        string root = Directory.CreateDirectory(Path.Combine(_parent, "junction-root")).FullName;
        string outsideDir = Directory.CreateDirectory(Path.Combine(_parent, "junction-outside")).FullName;
        string secret = Path.Combine(outsideDir, "hidden.dll");
        File.WriteAllBytes(secret, [4]);
        string junction = Path.Combine(root, "linked");
        CreateJunction(junction, outsideDir);

        ProcessModuleTools tools = Modules(root, new FakeModules(ProcessModuleQuery.Ok(null, false, false, 0,
        [
            new LoadedModuleRecord("hidden.dll", 1, 1, "x64", null, null, Path.Combine(junction, "hidden.dll"))
        ])));
        OperationResult<ProcessModuleList> result = tools.ListProcessModules(8, includePaths: true);

        Assert.Null(result.Value!.Modules[0].Path);
        Assert.True(result.Value.Modules[0].PathOutsideRoots);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListProcessModules_ChecksIdentityAndDoesNotLogFailurePaths()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-03-04T05:06:07Z");
        string secret = Path.Combine(_parent, "C-secret-module.dll");
        RecordingLogger logger = new();
        ProcessModuleTools tools = Modules(
            Directory.CreateDirectory(Path.Combine(_parent, "id")).FullName,
            new FakeModules(ProcessModuleQuery.Ok(start, false, false, 0, [])),
            logger);

        OperationResult<ProcessModuleList> mismatch = tools.ListProcessModules(9, expectedStartTime: start.AddSeconds(2));
        OperationResult<ProcessModuleList> match = tools.ListProcessModules(9, expectedStartTime: start);
        OperationResult<ProcessModuleList> denied = Modules(
            Directory.CreateDirectory(Path.Combine(_parent, "denied")).FullName,
            new FakeModules(ProcessModuleQuery.Fail("access_denied", "The process could not be opened.")),
            logger).ListProcessModules(3);

        Assert.Equal("process_identity_mismatch", mismatch.ErrorCode);
        Assert.DoesNotContain(secret, mismatch.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(match.Success, match.Error);
        Assert.Equal("access_denied", denied.ErrorCode);
        Assert.DoesNotContain(secret, denied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, string.Join('\n', logger.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListProcessModules_ClampsLimitAndReportsErrorsWithoutPaths()
    {
        FakeModules source = new(ProcessModuleQuery.Fail("process_not_found", "The process is not running."));
        RecordingLogger logger = new();
        ProcessModuleTools tools = Modules(Directory.CreateDirectory(Path.Combine(_parent, "clamp")).FullName, source, logger, maxModules: 3);

        OperationResult<ProcessModuleList> missing = tools.ListProcessModules(4, limit: 90);
        Assert.Equal("process_not_found", missing.ErrorCode);
        Assert.Equal(3, source.Limit);
        Assert.DoesNotContain("\\", missing.Error);
        Assert.All(logger.Lines, line => Assert.DoesNotContain("\\", line));
    }

    [Fact]
    public void DiagnosticsModule_IsOptInAndWarnsWithoutTokens()
    {
        VestigoOptions defaults = new();
        VestigoOptions enabled = new() { Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } } };
        VestigoOptions online = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowOnlineRevocation = true }
        };

        Assert.False(new DiagnosticsModule().EnabledByDefault);
        Assert.DoesNotContain(ModuleRegistry.Enabled(defaults), module => module.Name == "Diagnostics");
        Assert.Contains("Diagnostics", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("SetupAPI", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("graphics adapters", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("TCP and UDP bindings", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("Media Foundation video encoders", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("Windows Error Reporting", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("Process dumps", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("Firewall inventory", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("Capture probes", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("Network probes", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("synthetic tone", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("online revocation", ModuleRegistry.GetPrivilegedModuleWarning(online));
        Assert.DoesNotContain("online revocation", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        VestigoOptions probes = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowCaptureProbes = true }
        };
        Assert.Contains("Capture probes", ModuleRegistry.GetPrivilegedModuleWarning(probes));
        VestigoOptions network = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowNetworkProbes = true }
        };
        Assert.Contains("Network probes", ModuleRegistry.GetPrivilegedModuleWarning(network));
        VestigoOptions playback = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowAudioPlayback = true }
        };
        Assert.Contains("synthetic tone", ModuleRegistry.GetPrivilegedModuleWarning(playback));
        VestigoOptions dumps = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowProcessDumps = true }
        };
        Assert.Contains("Process dumps", ModuleRegistry.GetPrivilegedModuleWarning(dumps));
        VestigoOptions firewall = new()
        {
            Modules = { ["Diagnostics"] = new ModuleSettings { Enabled = true } },
            Diagnostics = { AllowFirewallInventory = true }
        };
        Assert.Contains("Firewall inventory", ModuleRegistry.GetPrivilegedModuleWarning(firewall));
        Assert.False(ModuleSelection.Defaults()["Diagnostics"]);
        Assert.Equal("Jobs", ModuleSelection.Find("Diagnostics")!.Parent);
        Assert.Equal(["Jobs"], new DiagnosticsModule().Dependencies);
    }

    [Fact]
    public void StartUnchanged_SkipsMissingOpenedTimeAndRejectsADifferentProcess()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-04-05T06:07:08Z");

        Assert.True(WindowsProcessModuleSource.StartUnchanged(null, null));
        Assert.True(WindowsProcessModuleSource.StartUnchanged(null, start));
        Assert.True(WindowsProcessModuleSource.StartUnchanged(start, start));
        Assert.False(WindowsProcessModuleSource.StartUnchanged(start, null));
        Assert.False(WindowsProcessModuleSource.StartUnchanged(start, start.AddSeconds(2)));
    }

    [Fact]
    public void BoundedReadStream_StopsAtTheCapturedLength()
    {
        using MemoryStream inner = new([1, 2, 3, 4, 5]);
        using BoundedReadStream view = new(inner, 3);
        byte[] buffer = new byte[5];

        Assert.Equal(3, view.Length);
        Assert.Equal(3, view.Read(buffer, 0, 5));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0 }, buffer);
        Assert.Throws<IOException>(() => view.Seek(4, SeekOrigin.Begin));
        Assert.Equal(1, view.Seek(-2, SeekOrigin.End));
        Assert.Equal(2, view.Read(buffer));
        Assert.Equal(2, buffer[0]);
        Assert.Equal(3, buffer[1]);
    }

    [Fact]
    public void DiagnosticsOptions_RejectsOutOfRangeBounds()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxProcessModules = 0 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxInspectBytes = 10 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxDumpBytes = 10 } }.Validate());
        new VestigoOptions().Validate();
    }

    private static void AssertImage(ushort machine, bool hybrid, string architecture)
    {
        BinaryInspection image = Inspect(Image(machine, hybrid));
        Assert.Equal(architecture, image.Architecture);
        Assert.Equal("PE32+", image.Format);
        Assert.Equal("windowsCui", image.Subsystem);
        Assert.Contains("dynamicBase", image.Characteristics);
        Assert.Contains("nx", image.Characteristics);
        Assert.Contains("controlFlowGuard", image.Characteristics);
        Assert.Contains("highEntropyVa", image.Characteristics);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x60000000), image.Timestamp);
        Assert.False(image.Managed);
    }

    private static void AssertOutcome(int embedded, CatalogLookup? catalog, string outcome, string source)
    {
        SignatureDecisionResult decision = SignatureDecision.Decide(embedded, catalog);
        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(source, decision.Source);
    }

    private static BinaryInspection Inspect(byte[] pe, bool computeSha256 = false)
    {
        using MemoryStream stream = new(pe);
        OperationResult<BinaryInspection> result = PeImage.Inspect(stream, computeSha256);
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    private static byte[] Image(ushort machine, bool hybrid)
    {
        byte[] pe = MinimalPe.WithoutCodeView();
        BitConverter.GetBytes(machine).CopyTo(pe, 0x84);
        BitConverter.GetBytes(0x60000000).CopyTo(pe, 0x88);
        BitConverter.GetBytes((ushort)0x4160).CopyTo(pe, 0x98 + 70);
        if (!hybrid)
        {
            return pe;
        }

        BitConverter.GetBytes(0x1000).CopyTo(pe, 0x98 + 112 + (10 * 8));
        BitConverter.GetBytes(208).CopyTo(pe, 0x98 + 112 + (10 * 8) + 4);
        BitConverter.GetBytes(1L).CopyTo(pe, 0x200 + 200);
        return pe;
    }

    private BinaryTools Tools(string root, long maxInspectBytes = 256L * 1024 * 1024, ISignatureVerifier? verifier = null, bool allowOnline = false)
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "work", Path = root, Access = RootAccess.Read }],
            Diagnostics = { AllowOnlineRevocation = allowOnline, MaxInspectBytes = maxInspectBytes }
        };
        return new BinaryTools(new RootPolicy(options), new PeInspector(), verifier ?? new CountingVerifier(), options);
    }

    private static ProcessModuleTools Modules(
        string root,
        IProcessModuleSource source,
        RecordingLogger? logger = null,
        int maxModules = 500) 
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "work", Path = root, Access = RootAccess.Read }],
            Diagnostics = { MaxProcessModules = maxModules }
        };
        return new ProcessModuleTools(new RootPolicy(options), source, options, logger ?? new RecordingLogger());
    }

    private static void RemoveDirectoryLinks(string directory)
    {
        foreach (string entry in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(entry);
            }
            else
            {
                RemoveDirectoryLinks(entry);
            }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class CountingVerifier : ISignatureVerifier
    {
        public int Calls { get; private set; }

        public RevocationMode Mode { get; private set; }

        public bool IncludeSigner { get; private set; }

        public string? Path { get; private set; }

        public SignatureVerification Verify(string path, RevocationMode revocation, bool includeSigner)
        {
            Calls++;
            Mode = revocation;
            IncludeSigner = includeSigner;
            Path = path;
            return new SignatureVerification("unsigned", "none", null, RevocationModes.Format(revocation), DateTimeOffset.UnixEpoch, null);
        }
    }

    private sealed class FakeModules(ProcessModuleQuery result) : IProcessModuleSource
    {
        public int Limit { get; private set; }

        public ProcessModuleQuery Query(int pid, int limit)
        {
            Limit = limit;
            return result;
        }
    }

    private sealed class RecordingLogger : ILogger<ProcessModuleTools>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
