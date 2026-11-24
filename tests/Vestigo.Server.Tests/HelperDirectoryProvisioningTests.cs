using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class HelperDirectoryProvisioningTests
{
    private const string Spool = @"C:\ProgramData\Nefarius Software Solutions\Vestigo\spool";
    private static readonly string Helper = HelperDirectoryProvisioning.HelperPath(Spool);
    private const string Admins = "S-1-5-32-544";
    private const string User = "S-1-5-21-1-2-3-1001";

    private static HelperDirectorySecurityState Trusted => new(Admins, true, [], true, true);

    private static HelperDirectorySecurityState Stale(string owner = User, bool protectedAcl = false) =>
        new(owner, protectedAcl, [], true, true);

    [Fact]
    public void FreshInstall_ProvisionsAfterSpool()
    {
        FakeStore store = new();

        Assert.Equal(HelperDirectoryState.NeedsProvision, HelperDirectoryProvisioning.Prepare(Spool, store));
        HelperDirectoryProvisioning.Provision(Spool, store);

        Assert.Equal([Helper], store.Created);
        Assert.Null(store.Security[Helper].Untrusted());
    }

    [Theory]
    [InlineData(Admins)]
    [InlineData("S-1-5-18")]
    public void RepairWithATrustedHelper_LeavesItAndItsContentsAlone(string owner)
    {
        FakeStore store = new();
        store.AddDirectory(Helper, new(owner, true, [], true, true));
        store.AddFile(Path.Combine(Helper, "notes.txt"));

        Assert.Equal(HelperDirectoryState.AlreadySecure, HelperDirectoryProvisioning.Prepare(Spool, store));

        Assert.Empty(store.Deleted);
        Assert.Empty(store.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleHelperWithAnUntrustedOwner_IsReplacedWhenOnlyScratchFilesRemain(bool withScratchFiles)
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Stale());
        string[] scratch =
        [
            Guid.NewGuid().ToString("N") + ".json",
            Guid.NewGuid().ToString("N").ToUpperInvariant() + ".events",
            "desktop-host-" + Guid.NewGuid().ToString("N") + ".log"
        ];
        if (withScratchFiles)
        {
            foreach (string name in scratch)
            {
                store.AddFile(Path.Combine(Helper, name));
            }
        }

        Assert.Equal(HelperDirectoryState.NeedsProvision, HelperDirectoryProvisioning.Prepare(Spool, store));
        Assert.DoesNotContain(Helper, store.Created);
        HelperDirectoryProvisioning.Provision(Spool, store);

        Assert.Equal(withScratchFiles ? 3 : 0, store.Deleted.Count(path => path != Helper));
        Assert.Contains(Helper, store.Deleted);
        Assert.Contains(Helper, store.Created);
        Assert.Null(store.Security[Helper].Untrusted());
        Assert.Contains(store.Logs, line => line.Contains(Helper, StringComparison.Ordinal) && line.Contains(User, StringComparison.Ordinal));
    }

    [Fact]
    public void StaleHelperWithATrustedOwnerButAnUnprotectedAcl_IsAlsoReplaced()
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Stale(Admins));

        Assert.Equal(HelperDirectoryState.NeedsProvision, HelperDirectoryProvisioning.Prepare(Spool, store));
        Assert.Contains(Helper, store.Deleted);
    }

    [Theory]
    [InlineData("file", "report.txt")]
    [InlineData("file", "0123456789abcdef.json")]
    [InlineData("file", "desktop-host-xyz.log")]
    [InlineData("directory", "nested")]
    [InlineData("link", "trap")]
    public void StaleHelperWithOtherContents_FailsClosedNamingPathAndReason(string kind, string name)
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Stale());
        string scratch = Path.Combine(Helper, Guid.NewGuid().ToString("N") + ".json");
        store.AddFile(scratch);
        string offender = Path.Combine(Helper, name);
        store.AddEntry(new HelperDirectoryEntry(offender, kind == "directory", kind == "link"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Prepare(Spool, store));

        Assert.Contains(Helper, ex.Message, StringComparison.Ordinal);
        Assert.Contains(offender, ex.Message, StringComparison.Ordinal);
        Assert.Contains("untrusted principal", ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.Deleted);
        Assert.Empty(store.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HelperThatIsALinkOrFile_IsRefusedWithoutChange(bool link)
    {
        FakeStore store = new();
        if (link)
        {
            store.AddDirectory(Helper, Trusted);
            store.Links.Add(Helper);
        }
        else
        {
            store.Files.Add(Helper);
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Prepare(Spool, store));

        Assert.Contains(Helper, ex.Message, StringComparison.Ordinal);
        Assert.Contains(link ? "reparse point" : "is a file", ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.Deleted);
        Assert.Empty(store.Created);
    }

    [Fact]
    public void Provision_RejectsAnUntrustedResultAndAnAppearingDirectory()
    {
        FakeStore weak = new() { CreatedSecurity = Stale() };
        Assert.Contains("does not meet", Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Provision(Spool, weak)).Message);

        FakeStore appeared = new();
        appeared.AddDirectory(Helper, Stale());
        Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Provision(Spool, appeared));
        Assert.Empty(appeared.Created);
    }

    [Fact]
    public void Complete_ChecksTheLiveHelperAfterSpoolIsSecured()
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Trusted);
        Assert.Equal(HelperDirectoryState.AlreadySecure, HelperDirectoryProvisioning.Prepare(Spool, store));

        // Replaced by an untrusted principal while the parent ACLs were being rewritten.
        store.AddDirectory(Helper, Stale());
        HelperDirectoryProvisioning.Complete(Spool, store, HelperDirectoryState.AlreadySecure);

        Assert.Contains(Helper, store.Created);
        Assert.Null(store.Security[Helper].Untrusted());
    }

    [Fact]
    public void Complete_LeavesAnUnchangedTrustedHelperInPlace()
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Trusted);

        HelperDirectoryProvisioning.Complete(Spool, store, HelperDirectoryState.AlreadySecure);

        Assert.Empty(store.Created);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public void Cleanup_StopsWhenTheHelperBecomesALinkAfterTheListing()
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Stale());
        store.AddFile(Path.Combine(Helper, Guid.NewGuid().ToString("N") + ".json"));
        store.AddFile(Path.Combine(Helper, Guid.NewGuid().ToString("N") + ".events"));
        store.AfterDelete = () => store.Links.Add(Helper);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Prepare(Spool, store));

        Assert.Contains("changed while", ex.Message, StringComparison.Ordinal);
        Assert.Single(store.Deleted);
    }

    [Fact]
    public void Cleanup_StopsWhenAnEntryBecomesALinkAfterTheListing()
    {
        FakeStore store = new();
        store.AddDirectory(Helper, Stale());
        string entry = Path.Combine(Helper, Guid.NewGuid().ToString("N") + ".json");
        store.AddFile(entry);
        store.Links.Add(entry);

        // The listing saw an ordinary file; the live check sees a link.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HelperDirectoryProvisioning.Prepare(Spool, store));

        Assert.Empty(store.Deleted);
        Assert.Contains(entry, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF.json", true)]
    [InlineData("0123456789abcdef0123456789abcdef.events", true)]
    [InlineData("desktop-host-0123456789abcdef0123456789abcdef.log", true)]
    [InlineData("0123456789abcdef0123456789abcdef.log", false)]
    [InlineData("0123456789abcdef0123456789abcde.json", false)]
    [InlineData("0123456789abcdef0123456789abcdeg.json", false)]
    [InlineData("desktop-host-.log", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ScratchNames_AreStrict(string? name, bool expected) =>
        Assert.Equal(expected, HelperDirectoryProvisioning.IsDisposableScratchName(name));

    [Fact]
    public void InstallerAction_RunsBeforeStartServicesAndPreparesHelperBeforeAnyAclChange()
    {
        string root = FindRepositoryRoot();
        string listen = File.ReadAllText(Path.Combine(root, "installer", "ListenPage.cs"));
        string actions = File.ReadAllText(Path.Combine(root, "installer", "Vestigo.Installer.Actions", "CustomActions.cs"));

        Assert.Contains("Sequence(\"VestigoWriteSettings\", \"Before\", \"StartServices\", NotRemoving)", listen, StringComparison.Ordinal);
        Assert.Contains("const string NotRemoving = \"NOT (REMOVE~=\\\"ALL\\\")\"", listen, StringComparison.Ordinal);
        int prepare = actions.IndexOf("HelperDirectoryProvisioning.Prepare(", StringComparison.Ordinal);
        int firstAcl = actions.IndexOf("CompanyFolderSecurity(system, administrators)", StringComparison.Ordinal);
        int provision = actions.IndexOf("HelperDirectoryProvisioning.Complete(", StringComparison.Ordinal);
        int lastAcl = actions.IndexOf("info.SetAccessControl(security)", StringComparison.Ordinal);
        Assert.True(prepare > 0 && prepare < firstAcl, "Prepare must run before the first ACL change.");
        Assert.True(provision > lastAcl, "Provision must run after the managed directories are secured.");
    }

    [Fact]
    public void Windows_FreshProvisionCreatesAProtectedAdministratorsOwnedDirectory()
    {
        RequireWindowsAdministrator();
        using TempSpool spool = new();
        WindowsHelperDirectoryStore store = new(_ => { });

        Assert.Equal(HelperDirectoryState.NeedsProvision, HelperDirectoryProvisioning.Prepare(spool.Path, store));
        HelperDirectoryProvisioning.Provision(spool.Path, store);

        DirectorySecurity security = new DirectoryInfo(spool.Helper).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(Admins, ((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value);
        string[] sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).Distinct().Order().ToArray();
        Assert.Equal(["S-1-5-18", Admins], sids);
        Assert.Equal(HelperDirectoryState.AlreadySecure, HelperDirectoryProvisioning.Prepare(spool.Path, store));
    }

    [Fact]
    public void Windows_OrdinaryHelperWithScratchFilesIsReplacedByAProtectedOne()
    {
        RequireWindowsAdministrator();
        using TempSpool spool = new();
        Directory.CreateDirectory(spool.Helper);
        string scratch = Path.Combine(spool.Helper, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(scratch, "{}");
        List<string> log = [];
        WindowsHelperDirectoryStore store = new(log.Add);

        // An ordinary directory inherits its ACL, so it is stale whatever its owner is.
        Assert.False(new DirectoryInfo(spool.Helper).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(HelperDirectoryState.NeedsProvision, HelperDirectoryProvisioning.Prepare(spool.Path, store));
        HelperDirectoryProvisioning.Provision(spool.Path, store);

        Assert.False(File.Exists(scratch));
        Assert.True(new DirectoryInfo(spool.Helper).GetAccessControl().AreAccessRulesProtected);
        Assert.Contains(log, line => line.Contains(spool.Helper, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_OrdinaryHelperWithOtherContentIsKept()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs only.");
        }

        using TempSpool spool = new();
        Directory.CreateDirectory(spool.Helper);
        string other = Path.Combine(spool.Helper, "keep.txt");
        File.WriteAllText(other, "x");
        string before = new DirectoryInfo(spool.Helper).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HelperDirectoryProvisioning.Prepare(spool.Path, new WindowsHelperDirectoryStore(_ => { })));

        Assert.Contains(other, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(other));
        Assert.Equal(before, new DirectoryInfo(spool.Helper).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Windows_JunctionHelperIsRefusedAndItsTargetUntouched()
    {
        using TempSpool spool = new();
        string target = Directory.CreateDirectory(Path.Combine(spool.Root, "target")).FullName;
        string kept = Path.Combine(target, "kept.txt");
        File.WriteAllText(kept, "x");
        CreateJunction(spool.Helper, target);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HelperDirectoryProvisioning.Prepare(spool.Path, new WindowsHelperDirectoryStore(_ => { })));

        Assert.Contains("reparse point", ex.Message, StringComparison.Ordinal);
        Assert.Contains(spool.Helper, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(kept));
    }

    private static void RequireWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs only.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Skip("Setting the Administrators owner needs an elevated process, as the MSI action has.");
        }
    }

    private static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }

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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class TempSpool : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("vestigo-helper").FullName;

        public string Path { get; }

        public string Helper => HelperDirectoryProvisioning.HelperPath(Path);

        public TempSpool()
        {
            Path = System.IO.Path.Combine(Root, "spool");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Helper) && (File.GetAttributes(Helper) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(Helper);
                }

                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private sealed class FakeStore : IHelperDirectoryStore
    {
        public Dictionary<string, HelperDirectorySecurityState> Security { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<string> Logs { get; } = [];

        public Action? AfterDelete { get; set; }

        public HelperDirectorySecurityState CreatedSecurity { get; set; } = new(Admins, true, [], true, true);

        private readonly List<HelperDirectoryEntry> _entries = [];

        public void AddDirectory(string path, HelperDirectorySecurityState security) => Security[path] = security;

        public void AddFile(string path) => AddEntry(new HelperDirectoryEntry(path, false, false));

        public void AddEntry(HelperDirectoryEntry entry) => _entries.Add(entry);

        public bool FileExists(string path) => Files.Contains(path);

        public bool DirectoryExists(string path) => Security.ContainsKey(path);

        public bool IsReparsePoint(string path) => Links.Contains(path);

        public HelperDirectorySecurityState ReadSecurity(string directory) => Security[directory];

        public IReadOnlyList<HelperDirectoryEntry> ListEntries(string directory) => [.. _entries];

        public void DeleteFile(string path)
        {
            Deleted.Add(path);
            _entries.RemoveAll(entry => entry.Path == path);
            AfterDelete?.Invoke();
        }

        public void DeleteEmptyDirectory(string path)
        {
            Assert.Empty(_entries);
            Deleted.Add(path);
            Security.Remove(path);
        }

        public void CreateSecured(string path)
        {
            Created.Add(path);
            Security[path] = CreatedSecurity;
        }

        public void Log(string message) => Logs.Add(message);
    }
}
