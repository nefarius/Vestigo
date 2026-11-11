using System.Security.Principal;
using System.Text;

using Win32 = Microsoft.Win32;

using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.RegistryProbe;
using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class RegistryTests
{
    private const string Sid = "S-1-5-21-1";

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Contoso", @"HKLM\SOFTWARE\Contoso")]
    [InlineData(@"hklm\software\contoso", @"HKLM\software\contoso")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", @"HKLM\SOFTWARE\Contoso")]
    [InlineData(@"HKLM/SOFTWARE/Contoso", @"HKLM\SOFTWARE\Contoso")]
    [InlineData(@"HKLM\\SOFTWARE\\Contoso\", @"HKLM\SOFTWARE\Contoso")]
    public void Policy_CanonicalizesHiveCaseAndSeparators(string path, string canonical)
    {
        RegistryPathDecision decision = RegistryAssert.Ok(RegistryPathPolicy.Check(path, Sid, Allowing(@"HKLM\SOFTWARE\Contoso")));

        Assert.Equal(canonical, decision.Canonical);
        Assert.Equal(RegistryHiveKind.LocalMachine, decision.Hive);
        Assert.Equal(canonical["HKLM\\".Length..], decision.SubKey);
    }

    [Fact]
    public void Policy_RequiresAPathBoundary()
    {
        RegistryOptions options = Allowing(@"HKLM\SOFTWARE\Contoso");

        Assert.True(RegistryPathPolicy.Check(@"HKLM\SOFTWARE\Contoso\Product", Sid, options).Success);
        Assert.Equal("path_not_allowed", RegistryPathPolicy.Check(@"HKLM\SOFTWARE\ContosoEvil", Sid, options).ErrorCode);
    }

    [Fact]
    public void Policy_DenyWinsAndCannotBeRemovedByAnAllowPrefix()
    {
        RegistryOptions options = Allowing(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");

        Assert.Equal(
            "path_denied",
            RegistryPathPolicy.Check(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\AutoAdminLogon", Sid, options).ErrorCode);
        Assert.Equal("path_denied", RegistryPathPolicy.Check(@"HKLM\SAM\SAM", Sid, options).ErrorCode);
    }

    [Fact]
    public void Policy_ResolvesHkcuAgainstTheConsoleSid()
    {
        RegistryOptions options = Allowing(@"HKCU\Software\Contoso");

        RegistryPathDecision decision = RegistryAssert.Ok(RegistryPathPolicy.Check(@"HKCU\Software\Contoso\Product", Sid, options));

        Assert.Equal($@"HKU\{Sid}\Software\Contoso\Product", decision.Canonical);
        Assert.Equal(RegistryHiveKind.Users, decision.Hive);
        Assert.Equal($@"{Sid}\Software\Contoso\Product", decision.SubKey);
        Assert.Equal("no_interactive_session", RegistryPathPolicy.Check(@"HKCU\Software\Contoso", null, options).ErrorCode);
    }

    [Theory]
    [InlineData(@"HKCR\Software")]
    [InlineData(@"HKEY_CLASSES_ROOT\Software")]
    [InlineData(@"HKCC\System")]
    [InlineData(@"HKLM\..\SAM")]
    public void Policy_RejectsUnsupportedHivesAndParentSegments(string path)
    {
        OperationResult<RegistryPathDecision> result = RegistryPathPolicy.Check(path, Sid, new RegistryOptions());

        Assert.Equal("invalid_request", result.ErrorCode);
        Assert.DoesNotContain("invalid_hive", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_EmptyAllowListUsesBuiltInPrefixesOnly()
    {
        RegistryOptions options = new();

        Assert.True(RegistryPathPolicy.Check(@"HKLM\SYSTEM\CurrentControlSet\Services\Contoso", Sid, options).Success);
        Assert.Equal("path_not_allowed", RegistryPathPolicy.Check(@"HKLM\SOFTWARE\Contoso", Sid, options).ErrorCode);
    }

    [Fact]
    public void Read_ReturnsJsonNullStyleAbsenceForOmittedBinaryAndHashesWhatItRead()
    {
        FakeStore store = new();
        store.Add(@"SOFTWARE\Contoso", "", Raw("REG_SZ", System.Text.Encoding.Unicode.GetBytes("pending")));
        store.Add(@"SOFTWARE\Contoso", "Flag", Raw("REG_DWORD", BitConverter.GetBytes(7)));
        byte[] binary = [1, 2, 3, 4];
        store.Add(@"SOFTWARE\Contoso", "Blob", Raw("REG_BINARY", binary));
        RegistryTools tools = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"));

        RegistryValueResult text = tools.ReadRegistryValue(@"HKLM\SOFTWARE\Contoso").Value!;
        RegistryValueResult number = tools.ReadRegistryValue(@"HKLM\SOFTWARE\Contoso", "Flag").Value!;
        RegistryValueResult hidden = tools.ReadRegistryValue(@"HKLM\SOFTWARE\Contoso", "Blob").Value!;
        RegistryValueResult shown = tools.ReadRegistryValue(@"HKLM\SOFTWARE\Contoso", "Blob", includeBinary: true).Value!;

        Assert.Null(text.Name);
        Assert.Equal("pending", text.Text);
        Assert.Equal(7ul, number.Number);
        Assert.Null(hidden.Hex);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary)), hidden.Sha256);
        Assert.Equal(Convert.ToHexString(binary), shown.Hex);
        Assert.Equal("registry64", hidden.View);
    }

    [Fact]
    public void Read_RejectsAnOversizedBinaryWithoutReturningIt()
    {
        FakeStore store = new();
        store.Add(@"SOFTWARE\Contoso", "Blob", new RegistryRawValue(true, null, null, "REG_BINARY", 4097, null, true));
        RegistryTools tools = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"));

        OperationResult<RegistryValueResult> result = tools.ReadRegistryValue(@"HKLM\SOFTWARE\Contoso", "Blob", includeBinary: true);

        Assert.Equal("value_too_large", result.ErrorCode);
        Assert.DoesNotContain("Blob", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public void List_StopsAtTheEntryCapAndOmitsDeniedChildren()
    {
        FakeStore store = new();
        FakeKey key = store.Add(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "A", Raw("REG_SZ", "one"u8.ToArray()));
        key.Values["B"] = Raw("REG_SZ", "two"u8.ToArray());
        key.SubKeys.Add("Winlogon");
        key.SubKeys.Add("Other");
        RegistryOptions options = Allowing(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        RegistryTools tools = Tools(store, options);

        RegistryListResult listed = tools.ListRegistryValues(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            recurse: 1,
            maxEntries: 10).Value!;

        Assert.Equal(["A", "B"], listed.Keys[0].Values.Select(item => item.Name));
        Assert.Equal(["Other"], listed.Keys[0].SubKeys.Select(item => item.Name));
        Assert.False(listed.Truncated);
    }

    [Fact]
    public void List_ReportsAMissingKeyInsteadOfAnEmptySuccess()
    {
        RegistryTools tools = Tools(new FakeStore(), Allowing(@"HKLM\SOFTWARE\Contoso"));

        OperationResult<RegistryListResult> missing = tools.ListRegistryValues(@"HKLM\SOFTWARE\Contoso");

        Assert.False(missing.Success);
        Assert.Equal("key_not_found", missing.ErrorCode);
        Assert.Null(missing.Value);
    }

    [Fact]
    public void List_KeepsTheParentWhenARecursiveChildCannotBeOpened()
    {
        FakeStore store = new();
        FakeKey key = store.Add(@"SOFTWARE\Contoso", "A", Raw("REG_SZ", "one"u8.ToArray()));
        key.SubKeys.Add("Child");
        RegistryTools tools = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"));

        RegistryListResult listed = tools.ListRegistryValues(@"HKLM\SOFTWARE\Contoso", recurse: 1).Value!;

        Assert.Equal(["Child"], listed.Keys[0].SubKeys.Select(item => item.Name));
        Assert.Single(listed.Keys);
    }

    [Fact]
    public void List_DoesNotSpendTheEntryBudgetOnDeniedChildren()
    {
        FakeStore store = new();
        FakeKey key = store.Add(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "A", Raw("REG_SZ", "one"u8.ToArray()));
        key.SubKeys.Add("Winlogon");
        key.SubKeys.Add("Other");
        key.SubKeys.Add("Later");
        RegistryTools tools = Tools(store, Allowing(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"));

        RegistryListResult oneChild = tools.ListRegistryValues(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            maxEntries: 3).Value!;
        RegistryListResult bothChildren = tools.ListRegistryValues(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            maxEntries: 4).Value!;

        Assert.Equal(["Other"], oneChild.Keys[0].SubKeys.Select(item => item.Name));
        Assert.True(oneChild.Truncated);
        Assert.Equal(["Other", "Later"], bothChildren.Keys[0].SubKeys.Select(item => item.Name));
        Assert.False(bothChildren.Truncated);
    }

    [Fact]
    public void List_HonorsHkcuAllowAndDenyPrefixesWhileRecursing()
    {
        FakeStore store = new();
        string root = $@"{Sid}\Software\Contoso";
        FakeKey key = store.Add(root, "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("parent")));
        key.SubKeys.Add("Child");
        key.SubKeys.Add("Secret");
        store.Add(root + @"\Child", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("visible")));
        store.Add(root + @"\Secret", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("hidden")));
        RegistryOptions options = Allowing(@"HKCU\Software\Contoso");
        options.DeniedPrefixes.Add(@"HKCU\Software\Contoso\Secret");
        RegistryTools tools = Tools(store, options);

        RegistryListResult listed = tools.ListRegistryValues(@"HKCU\Software\Contoso", recurse: 1).Value!;

        Assert.Equal(2, listed.Total);
        Assert.Equal(2, listed.Keys.Count);
        Assert.Equal($@"HKU\{Sid}\Software\Contoso", listed.Keys[0].Path);
        Assert.Equal(["Flag"], listed.Keys[0].Values.Select(item => item.Name));
        Assert.Equal("parent", listed.Keys[0].Values[0].DisplayValue);
        Assert.Equal(["Child"], listed.Keys[0].SubKeys.Select(item => item.Name));
        Assert.Equal(1, listed.Keys[0].SubKeys[0].ValueCount);
        Assert.Equal($@"HKU\{Sid}\Software\Contoso\Child", listed.Keys[1].Path);
        Assert.Equal(["Flag"], listed.Keys[1].Values.Select(item => item.Name));
        Assert.Equal("visible", listed.Keys[1].Values[0].DisplayValue);
        Assert.DoesNotContain(listed.Keys, item => item.Path.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(listed.Truncated);
    }

    [Fact]
    public void Read_ChecksTheConsoleUsersHiveAgainstHkcuDenyPrefixes()
    {
        FakeStore store = new();
        store.Add($@"{Sid}\Software\Contoso", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("console")));
        store.Add(@"S-1-5-21-9\Software\Contoso", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("other")));
        RegistryOptions options = Allowing(@"HKU");
        options.DeniedPrefixes.Add(@"HKCU\Software\Contoso");
        RegistryTools tools = Tools(store, options);

        Assert.Equal("path_denied", tools.ReadRegistryValue($@"HKU\{Sid}\Software\Contoso", "Flag").ErrorCode);
        RegistryValueResult other = tools.ReadRegistryValue(@"HKU\S-1-5-21-9\Software\Contoso", "Flag").Value!;
        Assert.Equal("other", other.Text);

        store.ConsoleUser = new ConsoleUserLookup(
            null,
            "no_interactive_session",
            "No user is logged on at the console.",
            ConsoleSessionReasons.OnlyDisconnectedSessions);
        OperationResult<RegistryValueResult> missing = tools.ReadRegistryValue(@"HKCU\Software\Contoso", "Flag");
        Assert.Equal("no_interactive_session", missing.ErrorCode);
        Assert.Equal(ConsoleSessionReasons.OnlyDisconnectedSessions, missing.Reason);
        Assert.Equal("other", tools.ReadRegistryValue(@"HKU\S-1-5-21-9\Software\Contoso", "Flag").Value!.Text);
    }

    [Fact]
    public void Read_ReturnsSidLookupErrorsWhenAnHkcuDenyCannotBeResolved()
    {
        FakeStore store = new()
        {
            ConsoleUser = new ConsoleUserLookup(null, "access_denied", "Querying the console user requires the service account.")
        };
        store.Add(@"S-1-5-21-9\Software\Contoso", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("other")));
        RegistryOptions options = Allowing(@"HKU");
        options.DeniedPrefixes.Add(@"HKCU\Software\Contoso");
        RegistryTools tools = Tools(store, options);

        OperationResult<RegistryValueResult> result = tools.ReadRegistryValue(@"HKU\S-1-5-21-9\Software\Contoso", "Flag");

        Assert.Equal("access_denied", result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void List_ReturnsSidLookupErrorsWhenAnHkcuDenyCannotBeResolved()
    {
        FakeStore store = new()
        {
            ConsoleUser = new ConsoleUserLookup(null, "access_denied", "Querying the console user requires the service account.")
        };
        FakeKey key = store.Add(@"S-1-5-21-9\Software\Contoso", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("other")));
        key.SubKeys.Add("Child");
        store.Add(@"S-1-5-21-9\Software\Contoso\Child", "Flag", Raw("REG_SZ", Encoding.Unicode.GetBytes("nested")));
        RegistryOptions options = Allowing(@"HKU");
        options.DeniedPrefixes.Add(@"HKCU\Software\Contoso");
        RegistryTools tools = Tools(store, options);

        OperationResult<RegistryListResult> listed = tools.ListRegistryValues(@"HKU\S-1-5-21-9\Software\Contoso", recurse: 1);

        Assert.Equal("access_denied", listed.ErrorCode);
        Assert.Null(listed.Value);
    }

    [Fact]
    public void ParseLink_ReadsNtPathsAndRejectsMalformedTargets()
    {
        Assert.Equal(
            @"HKLM\SYSTEM\ControlSet001",
            RegistryPathPolicy.ParseLinkTarget(Nt(@"\Registry\Machine\SYSTEM\ControlSet001")).Value);
        Assert.Equal(
            $@"HKU\{Sid}\Software",
            RegistryPathPolicy.ParseLinkTarget(Nt($@"\Registry\User\{Sid}\Software")).Value);
        Assert.Equal("invalid_request", RegistryPathPolicy.ParseLinkTarget(Nt(@"\Registry\A\Software")).ErrorCode);
        Assert.Equal("invalid_request", RegistryPathPolicy.LinkTargetLength(4098).ErrorCode);
        Assert.Equal("invalid_request", RegistryPathPolicy.ParseLinkTarget([1, 2, 3]).ErrorCode);

        byte[] broken = Nt(@"\Registry\Machine\SOFTWARE");
        broken[2] = 0;
        broken[3] = 0;
        Assert.Equal("invalid_request", RegistryPathPolicy.ParseLinkTarget(broken).ErrorCode);
    }

    [Fact]
    public void Walker_DeniesTheTargetAtEverySegment()
    {
        Dictionary<string, byte[]?> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"SOFTWARE\Contoso\Middle"] = Nt(@"\Registry\Machine\SOFTWARE\Contoso\Safe"),
            [@"SOFTWARE\Contoso\Safe\Tail"] = Nt(@"\Registry\Machine\SECURITY\Policy"),
            [@"SOFTWARE\Contoso\Safe\Kept"] = null
        };

        Assert.Equal("path_denied", Walk(keys, @"HKLM\SOFTWARE\Contoso\Middle\Tail").ErrorCode);
        RegistryPathDecision kept = RegistryAssert.Ok(Walk(keys, @"HKLM\SOFTWARE\Contoso\Middle\Kept"));
        Assert.Equal(@"HKLM\SOFTWARE\Contoso\Safe\Kept", kept.Canonical);
    }

    [Fact]
    public void Walker_DenyWinsOnALinkTargetEvenWhenThatTargetIsAllowed()
    {
        Dictionary<string, byte[]?> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"SOFTWARE\Contoso\Alias"] = Nt(@"\Registry\Machine\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")
        };
        RegistryOptions options = Allowing(@"HKLM\SOFTWARE\Contoso");
        options.AllowedPrefixes.Add(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");

        Assert.Equal("path_denied", Walk(keys, @"HKLM\SOFTWARE\Contoso\Alias", options).ErrorCode);
    }

    [Fact]
    public void Walker_AllowsTheCurrentControlSetLinkOnlyForThatPath()
    {
        Dictionary<string, byte[]?> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"SYSTEM\CurrentControlSet"] = Nt(@"\Registry\Machine\SYSTEM\ControlSet001"),
            [@"SYSTEM\ControlSet001\Services\Contoso"] = null,
            [@"SOFTWARE\Contoso\Alias"] = Nt(@"\Registry\Machine\SYSTEM\ControlSet001\Services")
        };

        RegistryPathDecision services = RegistryAssert.Ok(Walk(keys, @"HKLM\SYSTEM\CurrentControlSet\Services\Contoso", new RegistryOptions()));
        Assert.Equal(@"HKLM\SYSTEM\ControlSet001\Services\Contoso", services.Canonical);
        Assert.Equal(
            "path_not_allowed",
            Walk(keys, @"HKLM\SOFTWARE\Contoso\Alias", Allowing(@"HKLM\SOFTWARE\Contoso")).ErrorCode);
    }

    [Fact]
    public void Walker_DeniesAControlSetTargetWhenTheLogicalPathIsDenied()
    {
        Dictionary<string, byte[]?> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"SYSTEM\CurrentControlSet"] = Nt(@"\Registry\Machine\SYSTEM\ControlSet001"),
            [@"SYSTEM\ControlSet001\Control\Lsa"] = null
        };
        RegistryOptions options = Allowing(@"HKLM\SYSTEM\CurrentControlSet");

        Assert.Equal(
            "path_denied",
            RegistryPathPolicy.CheckLink(
                @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
                @"HKLM\SYSTEM\ControlSet001\Control\Lsa",
                Sid,
                options).ErrorCode);
        Assert.Equal("path_denied", Walk(keys, @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa", options).ErrorCode);
    }

    [Fact]
    public void Walker_StopsALinkCycleAndAnOverlongChain()
    {
        Dictionary<string, byte[]?> cycle = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"SOFTWARE\Contoso\A"] = Nt(@"\Registry\Machine\SOFTWARE\Contoso\B"),
            [@"SOFTWARE\Contoso\B"] = Nt(@"\Registry\Machine\SOFTWARE\Contoso\A")
        };
        Assert.Equal("invalid_request", Walk(cycle, @"HKLM\SOFTWARE\Contoso\A").ErrorCode);

        Dictionary<string, byte[]?> chain = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index <= RegistryPathPolicy.MaxLinkHops; index++)
        {
            chain[$@"SOFTWARE\Contoso\L{index}"] = Nt($@"\Registry\Machine\SOFTWARE\Contoso\L{index + 1}");
        }

        Assert.Equal("invalid_request", Walk(chain, @"HKLM\SOFTWARE\Contoso\L0").ErrorCode);
    }

    [Fact]
    public void Read_DoesNotFollowALinkToADeniedKey()
    {
        FakeStore store = new();
        FakeKey link = store.Add(@"SOFTWARE\Contoso\Alias", "Ignored", Raw("REG_SZ", "nope"u8.ToArray()));
        link.LinkTarget = Nt(@"\Registry\Machine\SAM\SAM");
        store.Add(@"SAM\SAM", "Secret", Raw("REG_SZ", Encoding.Unicode.GetBytes("password")));

        OperationResult<RegistryValueResult> result = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"))
            .ReadRegistryValue(@"HKLM\SOFTWARE\Contoso\Alias", "Secret");

        Assert.Equal("path_denied", result.ErrorCode);
        Assert.DoesNotContain("password", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_FollowsALinkWhoseTargetIsAllowed()
    {
        FakeStore store = new();
        FakeKey link = store.Add(@"SOFTWARE\Contoso\Alias", "Ignored", Raw("REG_SZ", "nope"u8.ToArray()));
        link.LinkTarget = Nt(@"\Registry\Machine\SOFTWARE\Contoso\Real");
        store.Add(@"SOFTWARE\Contoso\Real", "Flag", Raw("REG_DWORD", BitConverter.GetBytes(7)));

        RegistryValueResult value = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"))
            .ReadRegistryValue(@"HKLM\SOFTWARE\Contoso\Alias", "Flag").Value!;

        Assert.Equal(7ul, value.Number);
        Assert.Equal(@"HKLM\SOFTWARE\Contoso\Real", value.Path);
    }

    [Fact]
    public void List_DoesNotReadADeniedLinkTarget()
    {
        FakeStore store = new();
        FakeKey parent = store.Add(@"SOFTWARE\Contoso", "Flag", Raw("REG_SZ", "parent"u8.ToArray()));
        parent.SubKeys.Add("Alias");
        FakeKey link = store.Add(@"SOFTWARE\Contoso\Alias", "Ignored", Raw("REG_SZ", "nope"u8.ToArray()));
        link.LinkTarget = Nt(@"\Registry\Machine\SAM\SAM");
        store.Add(@"SAM\SAM", "Secret", Raw("REG_SZ", Encoding.Unicode.GetBytes("password")));

        RegistryListResult listed = Tools(store, Allowing(@"HKLM\SOFTWARE\Contoso"))
            .ListRegistryValues(@"HKLM\SOFTWARE\Contoso", recurse: 2).Value!;

        Assert.Equal(["Alias"], listed.Keys[0].SubKeys.Select(item => item.Name));
        Assert.Null(listed.Keys[0].SubKeys[0].ValueCount);
        Assert.DoesNotContain(listed.Keys, key => key.Path.Contains("SAM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(listed.Keys, key => key.Values.Any(value => (value.DisplayValue ?? string.Empty).Contains("password", StringComparison.Ordinal)));
    }

    [Fact]
    public void List_OmitsADeniedChildAfterCurrentControlSetResolves()
    {
        FakeStore store = new();
        FakeKey link = store.Add(@"SYSTEM\CurrentControlSet", "Unused", Raw("REG_SZ", "x"u8.ToArray()));
        link.LinkTarget = Nt(@"\Registry\Machine\SYSTEM\ControlSet001");
        FakeKey control = store.Add(@"SYSTEM\ControlSet001\Control", "Flag", Raw("REG_SZ", "ok"u8.ToArray()));
        control.SubKeys.Add("Lsa");
        control.SubKeys.Add("Session Manager");
        store.Add(@"SYSTEM\ControlSet001\Control\Lsa", "Secret", Raw("REG_SZ", Encoding.Unicode.GetBytes("password")));
        RegistryOptions options = Allowing(@"HKLM\SYSTEM\CurrentControlSet");

        RegistryListResult listed = Tools(store, options)
            .ListRegistryValues(@"HKLM\SYSTEM\CurrentControlSet\Control", recurse: 1).Value!;

        Assert.Equal(@"HKLM\SYSTEM\ControlSet001\Control", listed.Keys[0].Path);
        Assert.Equal(["Session Manager"], listed.Keys[0].SubKeys.Select(item => item.Name));
        Assert.DoesNotContain(listed.Keys, key => key.Path.Contains("Lsa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Module_IsOptInAndWarnsWhenNoTokenIsConfigured()
    {
        VestigoOptions options = new();
        RegistryModule module = new();

        Assert.False(module.IsEnabled(options));
        options.Modules["Registry"] = new ModuleSettings { Enabled = true };
        ModuleRegistry.ValidateConfiguration(options);
        Assert.Contains("Registry", ModuleRegistry.GetPrivilegedModuleWarning(options), StringComparison.Ordinal);
        options.ApiTokens.Add("secret");
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(options));
        Assert.Throws<InvalidOperationException>(() => new RegistryOptions { MaxBinaryBytes = 0 }.Validate());
    }

    [Fact]
    public void WindowsStore_ReadsBuiltInPolicyMetadataWhenAccessible()
    {
        RegistryTools tools = Tools(new WindowsRegistryStore(), new RegistryOptions());

        OperationResult<RegistryMetadata> result = tools.GetRegistryMetadata(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");

        if (result.ErrorCode is "access_denied")
        {
            return;
        }

        Assert.True(result.Success, result.Error);
        Assert.Equal("registry64", result.Value!.View);
        Assert.True(result.Value.SubKeyCount >= 0);

        OperationResult<RegistryMetadata> controlSet = tools.GetRegistryMetadata(
            @"HKLM\SYSTEM\CurrentControlSet\Services");
        if (controlSet.ErrorCode is not "access_denied")
        {
            Assert.True(controlSet.Success, controlSet.Error);
            Assert.StartsWith(@"HKLM\SYSTEM\ControlSet", controlSet.Value!.Path, StringComparison.OrdinalIgnoreCase);
        }

        OperationResult<RegistryValueResult> value = tools.ReadRegistryValue(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
            "EnableLUA");
        Assert.True(value.Success || value.ErrorCode == "value_not_found", value.Error);
        if (value.Success)
        {
            Assert.Equal("REG_DWORD", value.Value!.Type);
            Assert.NotNull(value.Value.Number);
        }
    }

    [WindowsElevatedFact]
    public void WindowsStore_ReadsAndBoundsAScratchKey()
    {
        const string path = @"SOFTWARE\VestigoTest";
        using Win32.RegistryKey root = Win32.RegistryKey.OpenBaseKey(Win32.RegistryHive.LocalMachine, Win32.RegistryView.Registry64);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        using Win32.RegistryKey key = root.CreateSubKey(path);
        key.SetValue("Flag", 7, Win32.RegistryValueKind.DWord);
        key.SetValue("Blob", new byte[] { 1, 2, 3, 4, 5 }, Win32.RegistryValueKind.Binary);
        try
        {
            RegistryTools tools = Tools(new WindowsRegistryStore(), Allowing(@"HKLM\SOFTWARE\VestigoTest", maxBinaryBytes: 4));

            Assert.Equal(7ul, tools.ReadRegistryValue(@"HKLM\SOFTWARE\VestigoTest", "Flag", view: "registry64").Value!.Number);
            OperationResult<RegistryValueResult> binary = tools.ReadRegistryValue(@"HKLM\SOFTWARE\VestigoTest", "Blob");
            RegistryMetadata metadata = tools.GetRegistryMetadata(@"HKLM\SOFTWARE\VestigoTest").Value!;

            Assert.Equal("value_too_large", binary.ErrorCode);
            Assert.Null(binary.Value?.Sha256);
            Assert.True(metadata.ValueCount >= 2);
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    private static byte[] Nt(string path) => Encoding.Unicode.GetBytes(path + "\0");

    private static OperationResult<RegistryPathDecision> Walk(
        Dictionary<string, byte[]?> keys,
        string path,
        RegistryOptions? options = null)
    {
        options ??= Allowing(@"HKLM\SOFTWARE\Contoso");
        OperationResult<RegistryPathDecision> requested = RegistryPathPolicy.Check(path, Sid, options);
        if (!requested.Success || requested.Value is null)
        {
            return requested;
        }

        return RegistryLinkWalker.Resolve(
            requested.Value,
            (_, subKey) => Probe(keys, subKey),
            (logical, target) => RegistryPathPolicy.CheckLink(logical, target, Sid, options));
    }

    private static RegistryLinkProbe Probe(Dictionary<string, byte[]?> keys, string subKey)
    {
        if (keys.TryGetValue(subKey, out byte[]? target))
        {
            return target is null ? RegistryLinkProbe.NotALink() : RegistryLinkProbe.Link(target);
        }

        bool parent = keys.Keys.Any(path => path.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase));
        return parent
            ? RegistryLinkProbe.NotALink()
            : RegistryLinkProbe.Fail("key_not_found", "Registry key was not found.");
    }

    private static RegistryOptions Allowing(string prefix, int maxBinaryBytes = 4096) => new()
    {
        AllowedPrefixes = [prefix],
        MaxBinaryBytes = maxBinaryBytes
    };

    private static RegistryTools Tools(IRegistryStore store, RegistryOptions registry) =>
        new(new VestigoOptions { Registry = registry }, store);

    private static RegistryRawValue Raw(string type, byte[] data) =>
        new(true, null, null, type, data.Length, data, false);

    private sealed class FakeStore : IRegistryStore
    {
        private readonly Dictionary<string, FakeKey> _keys = new(StringComparer.OrdinalIgnoreCase);

        public ConsoleUserLookup ConsoleUser { get; set; } = new(Sid, null, null);

        public FakeKey Add(string subKey, string name, RegistryRawValue value)
        {
            if (!_keys.TryGetValue(subKey, out FakeKey? key))
            {
                key = new FakeKey(this, subKey);
                _keys[subKey] = key;
            }

            key.Values[name] = value;
            return key;
        }

        public FakeKey? Find(string subKey) => _keys.TryGetValue(subKey, out FakeKey? key) ? key : null;

        public ConsoleUserLookup LookupConsoleUser() => ConsoleUser;

        public RegistryOpen Open(RegistryViewKind view, RegistryHiveKind hive, string subKey, RegistryLinkConfinement confine)
        {
            string hiveName = hive == RegistryHiveKind.Users ? "HKU" : "HKLM";
            string canonical = subKey.Length == 0 ? hiveName : hiveName + "\\" + subKey;
            OperationResult<RegistryPathDecision> resolved = RegistryLinkWalker.Resolve(
                new RegistryPathDecision(canonical, hive, subKey),
                Probe,
                confine);
            if (!resolved.Success || resolved.Value is null)
            {
                return new RegistryOpen(null, resolved.ErrorCode, resolved.Error);
            }

            if (!_keys.TryGetValue(resolved.Value.SubKey, out FakeKey? key))
            {
                return new RegistryOpen(null, "key_not_found", "Registry key was not found.");
            }

            key.Bind(this, view, new RegistryPathDecision(canonical, hive, subKey), confine);
            return new RegistryOpen(key, null, null, resolved.Value);
        }

        private RegistryLinkProbe Probe(RegistryHiveKind hive, string subKey)
        {
            if (_keys.TryGetValue(subKey, out FakeKey? key))
            {
                return key.LinkTarget is null ? RegistryLinkProbe.NotALink() : RegistryLinkProbe.Link(key.LinkTarget);
            }

            bool parent = _keys.Keys.Any(path => path.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase));
            return parent
                ? RegistryLinkProbe.NotALink()
                : RegistryLinkProbe.Fail("key_not_found", "Registry key was not found.");
        }
    }

    private sealed class FakeKey(FakeStore store, string path) : IRegistryKey
    {
        public DateTimeOffset? LastWriteUtc => DateTimeOffset.UnixEpoch;

        public Dictionary<string, RegistryRawValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> SubKeys { get; } = [];

        public byte[]? LinkTarget { get; set; }

        private FakeStore? _store;
        private RegistryViewKind _view;
        private RegistryPathDecision? _path;
        private RegistryLinkConfinement? _confine;

        public int SubKeyCount => SubKeys.Count;

        public int ValueCount => Values.Count;

        public RegistryNamePage SubKeyNames(int limit) => Page(SubKeys, limit);

        public RegistryNamePage ValueNames(int limit) => Page(Values.Keys.ToList(), limit);

        public void Bind(FakeStore owner, RegistryViewKind view, RegistryPathDecision resolved, RegistryLinkConfinement confine)
        {
            _store = owner;
            _view = view;
            _path = resolved;
            _confine = confine;
        }

        public int? SubKeyValueCount(string name)
        {
            if (_store is null || _path is null || _confine is null)
            {
                return null;
            }

            string child = _path.SubKey.Length == 0 ? name : _path.SubKey + "\\" + name;
            RegistryOpen opened = _store.Open(_view, _path.Hive, child, _confine);
            if (opened.Key is null)
            {
                return null;
            }

            using IRegistryKey key = opened.Key;
            return key.ValueCount;
        }

        public RegistryRawValue Read(string? name, int maxBinaryBytes, int maxStringBytes) =>
            Values.TryGetValue(name ?? string.Empty, out RegistryRawValue? value)
                ? value
                : new RegistryRawValue(false, "value_not_found", "Registry value was not found.", "REG_NONE", 0, null, false);

        public void Dispose()
        {
        }

        private static RegistryNamePage Page(IReadOnlyList<string> names, int limit)
        {
            if (limit <= 0)
            {
                return new RegistryNamePage([], false);
            }

            return new RegistryNamePage(names.Take(limit).ToList(), names.Count > limit);
        }
    }

    private sealed class WindowsElevatedFactAttribute : FactAttribute
    {
        public WindowsElevatedFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires an elevated Windows session.";
                return;
            }

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Skip = "Requires an elevated Windows session.";
            }
        }
    }
}

file static class RegistryAssert
{
    public static RegistryPathDecision Ok(OperationResult<RegistryPathDecision> result)
    {
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }
}
