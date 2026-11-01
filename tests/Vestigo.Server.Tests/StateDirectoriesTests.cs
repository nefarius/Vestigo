using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class StateDirectoriesTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("vestigo-state").FullName;
    private readonly string _settings;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public StateDirectoriesTests()
    {
        _settings = Path.Combine(_base, "Vestigo");
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    [Fact]
    public void EnsureSecure_CreatesTheTreeWithAProtectedAcl()
    {
        Assert.Empty(StateDirectories.EnsureSecure());

        Assert.True(Directory.Exists(VestigoPaths.SpoolDirectory));
        Assert.True(Directory.Exists(VestigoPaths.LogDirectory));
        Assert.True(Directory.Exists(VestigoPaths.DefaultSymbolCache));
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DirectorySecurity security = new DirectoryInfo(VestigoPaths.SpoolDirectory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        string me = WindowsIdentity.GetCurrent().User!.Value;
        string[] allowed = ["S-1-5-18", "S-1-5-32-544", me];
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, allowed);
        }
    }

    [Fact]
    public void EnsureSecure_RepairsAPermissiveAclAndReportsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs only.");
        }

        StateDirectories.EnsureSecure();
        DirectoryInfo spool = new(VestigoPaths.SpoolDirectory);
        DirectorySecurity security = spool.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        spool.SetAccessControl(security);
        string child = Path.Combine(spool.FullName, "child.txt");
        File.WriteAllText(child, "x");
        FileInfo childInfo = new(child);
        FileSecurity childSecurity = childInfo.GetAccessControl();
        childSecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Modify,
            AccessControlType.Allow));
        childInfo.SetAccessControl(childSecurity);

        IReadOnlyList<string> repairs = StateDirectories.EnsureSecure();

        string me = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Single(repairs);
        Assert.Null(AclTrust.FindUntrustedWriter(spool.GetAccessControl(), FileSystemRights.WriteData, me));
        Assert.Null(AclTrust.FindUntrustedWriter(childInfo.GetAccessControl(), FileSystemRights.WriteData, me));
        Assert.Contains(
            childInfo.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => ((SecurityIdentifier)rule.IdentityReference).Value is "S-1-5-18" or "S-1-5-32-544");
        Assert.Empty(StateDirectories.EnsureSecure());
    }

    [Fact]
    public void EnsureSubdirectory_RejectsPathsOutsideTheStateTree()
    {
        StateDirectories.EnsureSecure();

        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSubdirectory(Path.Combine(_base, "elsewhere")));
        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSubdirectory(_settings + "-sibling"));
        Assert.True(Directory.Exists(StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.SpoolDirectory, "jobs"))));
    }

    [Fact]
    public void EnsureSubdirectory_RejectsAJunctionChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }

        StateDirectories.EnsureSecure();
        string target = Directory.CreateDirectory(Path.Combine(_base, "target")).FullName;
        string junction = Path.Combine(VestigoPaths.SpoolDirectory, "jobs");
        CreateJunction(junction, target);

        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSubdirectory(Path.Combine(junction, "x")));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void EnsureSecure_RejectsAJunctionInTheSettingsPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }

        string target = Directory.CreateDirectory(Path.Combine(_base, "target")).FullName;
        string link = Path.Combine(_base, "link");
        CreateJunction(link, target);
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", Path.Combine(link, "Vestigo"));

        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSecure());
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void EnsureSecure_RejectsAJunctionAmongRepairedChildren()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }

        StateDirectories.EnsureSecure();
        DirectoryInfo spool = new(VestigoPaths.SpoolDirectory);
        DirectorySecurity security = spool.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        spool.SetAccessControl(security);
        string target = Directory.CreateDirectory(Path.Combine(_base, "target")).FullName;
        CreateJunction(Path.Combine(VestigoPaths.SpoolDirectory, "trap"), target);

        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSecure());
    }

    [Fact]
    public void EnsureSubdirectory_RejectsASymbolicLinkWhenAvailable()
    {
        StateDirectories.EnsureSecure();
        string target = Directory.CreateDirectory(Path.Combine(_base, "target")).FullName;
        string link = Path.Combine(VestigoPaths.SpoolDirectory, "symlink");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic link privilege is not available on this machine.");
        }

        Assert.Throws<InvalidOperationException>(() => StateDirectories.EnsureSubdirectory(link));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        try
        {
            RemoveLinks(_base);
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static void RemoveLinks(string directory)
    {
        foreach (string entry in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(entry);
            }
            else
            {
                RemoveLinks(entry);
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
}
