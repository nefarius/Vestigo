using Vestigo.Configuration;
using Vestigo.EnvVars;

namespace Vestigo.Server.Tests;

public class EnvironmentModuleTests
{
    private sealed class FakeStore : IEnvironmentStore
    {
        public Dictionary<(EnvironmentScope, string), EnvironmentValue> Values { get; } = new();

        public int Broadcasts { get; private set; }

        public bool UserAvailable { get; set; } = true;

        private void CheckUser(EnvironmentScope scope)
        {
            if (scope == EnvironmentScope.User && !UserAvailable)
            {
                throw new UserNotAvailableException("No user is logged on at the console.");
            }
        }

        public EnvironmentValue? Get(EnvironmentScope scope, string name)
        {
            CheckUser(scope);
            return Values.TryGetValue((scope, name.ToUpperInvariant()), out EnvironmentValue? value) ? value : null;
        }

        public IReadOnlyDictionary<string, EnvironmentValue> GetAll(EnvironmentScope scope)
        {
            CheckUser(scope);
            return Values.Where(pair => pair.Key.Item1 == scope).ToDictionary(pair => pair.Key.Item2, pair => pair.Value);
        }

        public void Set(EnvironmentScope scope, string name, EnvironmentValue value)
        {
            CheckUser(scope);
            Values[(scope, name.ToUpperInvariant())] = value;
        }

        public bool Remove(EnvironmentScope scope, string name)
        {
            CheckUser(scope);
            return Values.Remove((scope, name.ToUpperInvariant()));
        }

        public bool BroadcastChange()
        {
            Broadcasts++;
            return true;
        }
    }

    private static (EnvironmentService Service, FakeStore Store) Create()
    {
        FakeStore store = new();
        return (new EnvironmentService(store, new VestigoOptions()), store);
    }

    [Fact]
    public void ListEditor_AppendSkipsDuplicatesAndKeepsOrder()
    {
        string result = EnvironmentListEditor.Add(@"C:\a;C:\b\", @"c:\B;C:\c;C:\c;""C:\a""", prepend: false, out bool changed);

        Assert.True(changed);
        Assert.Equal(@"C:\a;C:\b\;C:\c", result);
    }

    [Fact]
    public void ListEditor_PrependPutsNewEntriesFirst()
    {
        string result = EnvironmentListEditor.Add(@"C:\a;;C:\b", @"C:\x;C:\y", prepend: true, out bool changed);

        Assert.True(changed);
        Assert.Equal(@"C:\x;C:\y;C:\a;C:\b", result);
    }

    [Fact]
    public void ListEditor_ReportsNoChangeWhenEverythingPresent()
    {
        string result = EnvironmentListEditor.Add(@"C:\a;%SystemRoot%", @"%systemroot%", prepend: false, out bool changed);

        Assert.False(changed);
        Assert.Equal(@"C:\a;%SystemRoot%", result);
        Assert.Equal(@"C:\a", EnvironmentListEditor.Add(null, @"C:\a;", false, out _));
    }

    [Fact]
    public void Set_AppendWritesOnceAndBroadcasts()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.Values[(EnvironmentScope.Machine, "PATH")] = new EnvironmentValue(@"C:\a", false);

        OperationResult<EnvironmentChange> first = service.Set("machine", "Path", "append", @"C:\b", false);
        OperationResult<EnvironmentChange> second = service.Set("machine", "Path", "append", @"c:\B", false);

        Assert.True(first.Value!.Changed);
        Assert.Equal("sent", first.Value.Broadcast);
        Assert.False(second.Value!.Changed);
        Assert.Equal(@"C:\a;C:\b", store.Values[(EnvironmentScope.Machine, "PATH")].Value);
        Assert.Equal(1, store.Broadcasts);
    }

    [Fact]
    public void Set_ListOperationsPreserveExpandKind()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.Values[(EnvironmentScope.Machine, "PATH")] = new EnvironmentValue(@"%SystemRoot%", true);

        OperationResult<EnvironmentChange> result = service.Set("machine", "PATH", "prepend", @"C:\tools", false);

        Assert.Equal("REG_EXPAND_SZ", result.Value!.Kind);
        Assert.Equal(new EnvironmentValue(@"C:\tools;%SystemRoot%", true), store.Values[(EnvironmentScope.Machine, "PATH")]);
    }

    [Fact]
    public void Set_ExpandFlagWritesExpandableValue()
    {
        (EnvironmentService service, FakeStore store) = Create();

        service.Set("user", "TOOLS", "set", @"%USERPROFILE%\tools", expand: true);

        Assert.Equal(new EnvironmentValue(@"%USERPROFILE%\tools", true), store.Values[(EnvironmentScope.User, "TOOLS")]);
    }

    [Fact]
    public void Set_UnsetRemovesAndIsIdempotent()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.Values[(EnvironmentScope.User, "DEMO")] = new EnvironmentValue("1", false);

        OperationResult<EnvironmentChange> first = service.Set("user", "DEMO", "unset", null, false);
        OperationResult<EnvironmentChange> second = service.Set("user", "DEMO", "unset", null, false);

        Assert.True(first.Value!.Changed);
        Assert.False(second.Value!.Changed);
        Assert.Empty(store.Values);
        Assert.Equal(1, store.Broadcasts);
    }

    [Fact]
    public void Set_SameValueDoesNothing()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.Values[(EnvironmentScope.Machine, "DEMO")] = new EnvironmentValue("1", false);

        OperationResult<EnvironmentChange> result = service.Set("machine", "DEMO", "set", "1", false);

        Assert.False(result.Value!.Changed);
        Assert.Equal(0, store.Broadcasts);
    }

    [Theory]
    [InlineData("comspec")]
    [InlineData("SYSTEMROOT")]
    [InlineData("windir")]
    public void Set_RefusesProtectedNames(string name)
    {
        (EnvironmentService service, FakeStore store) = Create();

        Assert.Equal("protected_name", service.Set("machine", name, "set", "x", false).ErrorCode);
        Assert.Equal("protected_name", service.Set("process", name, "unset", null, false).ErrorCode);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void Set_HonoursConfiguredProtectedNames()
    {
        FakeStore store = new();
        VestigoOptions options = new() { Environment = new EnvironmentOptions { ProtectedNames = ["Secret"] } };
        EnvironmentService service = new(store, options);

        Assert.Equal("protected_name", service.Set("user", "SECRET", "set", "x", false).ErrorCode);
        Assert.True(service.Set("user", "COMSPEC", "set", "x", false).Success);
    }

    [Fact]
    public void Tools_ReportUserNotAvailableAndValidateInput()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.UserAvailable = false;

        Assert.Equal("user_not_available", service.Get("user", "PATH").ErrorCode);
        Assert.Equal("user_not_available", service.Set("user", "X", "set", "1", false).ErrorCode);
        Assert.Equal("user_not_available", service.List("user", null, true).ErrorCode);
        Assert.Equal("invalid_scope", service.Get("everything", "PATH").ErrorCode);
        Assert.Equal("invalid_operation", service.Set("machine", "X", "replace", "1", false).ErrorCode);
        Assert.Equal("invalid_value", service.Set("machine", "X", "append", null, false).ErrorCode);
        Assert.Equal("invalid_name", service.Set("machine", "A=B", "set", "1", false).ErrorCode);
        Assert.Equal("not_found", Create().Service.Get("machine", "MISSING").ErrorCode);
    }

    [Fact]
    public void Get_ReturnsKindAndListHonoursNamesOnly()
    {
        (EnvironmentService service, FakeStore store) = Create();
        store.Values[(EnvironmentScope.Machine, "ZED")] = new EnvironmentValue("%X%", true);
        store.Values[(EnvironmentScope.Machine, "ALPHA")] = new EnvironmentValue("a", false);

        Assert.Equal("REG_EXPAND_SZ", service.Get("machine", "zed").Value!.Kind);
        EnvironmentListing listing = service.List("machine", null, includeValues: false).Value!;

        Assert.Equal(["ALPHA", "ZED"], listing.Variables.Select(item => item.Name));
        Assert.All(listing.Variables, item => Assert.Equal(string.Empty, item.Value));
    }

    [Fact]
    public void ProcessScope_SetsAndReadsCurrentProcess()
    {
        EnvironmentService service = new(new RegistryEnvironmentStore(), new VestigoOptions());
        string name = "VESTIGO_TEST_" + Guid.NewGuid().ToString("N");

        Assert.True(service.Set("process", name, "set", "one", false).Success);
        Assert.True(service.Set("process", name, "append", "two", false).Success);
        Assert.Equal("one;two", service.Get("process", name).Value!.Value);
        Assert.Equal("skipped", service.Set("process", name, "unset", null, false).Value!.Broadcast);
        Assert.Equal("not_found", service.Get("process", name).ErrorCode);
    }

    [Fact]
    public void MachineScope_ReadsPathUnexpanded()
    {
        EnvironmentService service = new(new RegistryEnvironmentStore(), new VestigoOptions());

        OperationResult<EnvironmentVariableInfo> result = service.Get("machine", "Path");

        Assert.True(result.Success);
        Assert.StartsWith("REG_", result.Value!.Kind);
    }
}
