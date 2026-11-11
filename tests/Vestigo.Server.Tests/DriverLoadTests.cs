using Vestigo.Devices;
using Vestigo.Diagnostics;
using Vestigo.EventLogs;
using Vestigo.Security.Signatures;
using Vestigo.Services;

namespace Vestigo.Server.Tests;

public sealed class DriverLoadTests
{
    [Fact]
    public void Projection_NamesOnlyDocumentedLockdownAndHvciValues()
    {
        Assert.Equal("unlocked", CodeIntegrityProjection.LockdownName(0));
        Assert.Null(CodeIntegrityProjection.LockdownName(7));
        Assert.Equal(true, CodeIntegrityProjection.HvciEnabled(1));
        Assert.Equal(false, CodeIntegrityProjection.HvciEnabled(0));
        Assert.Null(CodeIntegrityProjection.HvciEnabled(2));
        Assert.Equal("contoso.sys", CodeIntegrityProjection.ImageFileName(@"\SystemRoot\System32\drivers\contoso.sys"));
        string? relative = CodeIntegrityProjection.ExpandImagePath(@"System32\drivers\contoso.sys");
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(
            Path.GetFullPath(Path.Combine(systemRoot, @"System32\drivers\contoso.sys")),
            relative);
        Assert.Equal("contoso.sys", CodeIntegrityProjection.ImageFileName("\"C:\\Windows\\System32\\drivers\\contoso.sys\" -k"));
        Assert.Null(CodeIntegrityProjection.ImageFileName(@"C:\Windows\System32\drivers\..\evil.sys"));
        Assert.Null(CodeIntegrityProjection.ImageFileName("cmd.exe"));
    }

    [Fact]
    public void Projection_KeepsAMatchingCodeIntegrityEventAndDropsAnotherFile()
    {
        string xml = """
            <Event><EventData>
            <Data Name="FileNameBuffer">\Device\HarddiskVolume3\Windows\System32\drivers\contoso.sys</Data>
            <Data Name="ProcessNameBuffer">System</Data>
            <Data Name="RequestedPolicy">8</Data>
            <Data Name="Status">3221226541</Data>
            </EventData></Event>
            """;

        CodeIntegrityEvent? match = CodeIntegrityProjection.ParseEvent(xml, 3033, null, "ignored", "contoso.sys");
        Assert.NotNull(match);
        Assert.Equal(3033, match.Id);
        Assert.Equal("8", match.RequestedPolicy);
        Assert.Equal("3221226541", match.Status);
        Assert.Null(CodeIntegrityProjection.ParseEvent(xml, 3033, null, "contoso.sys", "other.sys"));
    }

    [Fact]
    public void Rules_CiteACodeIntegrityEventWithoutAssertingACause()
    {
        CodeIntegrityEvent entry = new(3033, null, @"\Windows\System32\drivers\contoso.sys", "System", "8", "3221226541");
        DeviceProblemEvidence evidence = new(null, null, null, Running("contoso"), null, null, null, [], null, null, null)
        {
            HvciEnabledValue = 1,
            HvciEnabled = true,
            WindowsLockdownMode = 0,
            WindowsLockdownModeName = "unlocked",
            CodeIntegrityEvents = [entry],
            Signature = new SignatureVerification("unsigned", "none", null, "cacheOnly", DateTimeOffset.UnixEpoch, null)
        };

        DriverLoadReport report = DeviceProblemRules.ExplainLoad(evidence, "contoso", "contoso.sys");

        Assert.False(report.InsufficientEvidence);
        Assert.Null(report.Explanation);
        Assert.Contains(report.Hypotheses, item => item.Label == "code-integrity" && item.Statement.Contains("3033", StringComparison.Ordinal));
        Assert.Contains(report.Hypotheses, item => item.Label == "image-signature");
        string text = string.Join('\n', report.Hypotheses.Select(item => item.Statement));
        Assert.DoesNotContain("caused", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("because", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Observed, fact => fact.Source == "code-integrity" && fact.Fact.Contains("hvciEnabled=True", StringComparison.Ordinal));

        CodeIntegrityEvent? byMessage = CodeIntegrityProjection.ParseEvent(null, 3077, null, "The image contoso.sys did not meet the policy.", "contoso.sys");
        Assert.NotNull(byMessage);
        Assert.Null(byMessage.FileName);
        DriverLoadReport cited = DeviceProblemRules.ExplainLoad(evidence with { CodeIntegrityEvents = [byMessage] }, "contoso", "contoso.sys");
        Assert.Contains(cited.Hypotheses, item => item.Label == "code-integrity" && item.Statement.Contains("3077", StringComparison.Ordinal));
        DriverLoadReport other = DeviceProblemRules.ExplainLoad(
            evidence with { CodeIntegrityEvents = [entry with { FileName = "other.sys" }] },
            "contoso",
            "contoso.sys");
        Assert.DoesNotContain(other.Hypotheses, item => item.Label == "code-integrity");
    }

    [Fact]
    public void Rules_DoNotTreatHvciOrAValidSignatureAsALoadFailure()
    {
        DeviceProblemEvidence evidence = new(null, null, null, Running("contoso"), null, null, null, [], null, null, null)
        {
            HvciEnabledValue = 1,
            HvciEnabled = true,
            WindowsLockdownMode = 7,
            Signature = new SignatureVerification("valid", "catalog", null, "cacheOnly", DateTimeOffset.UnixEpoch, null)
        };

        DriverLoadReport report = DeviceProblemRules.ExplainLoad(evidence, "contoso", "contoso.sys");

        Assert.True(report.InsufficientEvidence);
        Assert.Null(report.Explanation);
        Assert.Empty(report.Hypotheses);
        Assert.Contains(report.Observed, fact => fact.Fact.Contains("windowsLockdownMode=7", StringComparison.Ordinal) && fact.Fact.Contains("windowsLockdownModeName=null", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_KeepTheDocumentedLoadCodeAndAMissingService()
    {
        DeviceProblemEvidence evidence = new(null, null, null, null, "not_found", null, null, [], null, null, null);
        DriverLoadReport missing = DeviceProblemRules.ExplainLoad(evidence, "contoso", null);

        Assert.False(missing.InsufficientEvidence);
        Assert.Contains(missing.Hypotheses, item => item.Label == "service-state" && item.Evidence.Contains("service:contoso"));
        Assert.DoesNotContain("caused", missing.Hypotheses[0].Statement, StringComparison.OrdinalIgnoreCase);

        DeviceDetails device = new(@"ROOT\VESTIGOTEST\0000", null, [], [], "Test", "Test", Guid.Empty, "contoso", true, 39, 0, null, null, null, "1.2.3.4", null, @"C:\Windows\INF\oem7.inf");
        DeviceProblemEvidence failed = new(device, null, null, Running("contoso"), null, null, null, [], null, null, null);
        DriverLoadReport load = DeviceProblemRules.ExplainLoad(failed, "contoso", "contoso.sys");
        Assert.False(load.InsufficientEvidence);
        Assert.Contains("CM_PROB_DRIVER_FAILED_LOAD", load.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("caused", load.Explanation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_ReadsHvciAndLockdownWithoutThrowing()
    {
        CodeIntegrityPresence presence = new WindowsCodeIntegrity().Read();

        Assert.True(presence.HvciEnabled is null || presence.HvciEnabledValue is 0 or 1);
        if (presence.WindowsLockdownMode is int mode)
        {
            Assert.Equal(CodeIntegrityProjection.LockdownName(mode), presence.WindowsLockdownModeName);
        }
    }

    private static ServiceInfo Running(string name) =>
        new(name, name, "driver", "KERNEL_DRIVER", ServiceStates.Running, "system", @"\SystemRoot\System32\drivers\contoso.sys", null, []);
}
