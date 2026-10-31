using System.Text.RegularExpressions;

using Vestigo.Build;

namespace Vestigo.Server.Tests;

public class NefconPinTests
{
    [Fact]
    public void Pin_IsWellFormed()
    {
        Assert.Matches("^[0-9a-f]{64}$", NefconPin.ArchiveSha256);
        Assert.True(Uri.TryCreate(NefconPin.ArchiveUrl, UriKind.Absolute, out Uri? url));
        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Matches(@"^\d+\.\d+\.\d+$", NefconPin.Version);
        Assert.Contains($"/v{NefconPin.Version}/", NefconPin.ArchiveUrl, StringComparison.Ordinal);
        Assert.EndsWith($"nefcon_v{NefconPin.Version}.zip", NefconPin.ArchiveUrl, StringComparison.Ordinal);
        Assert.Equal("x64/nefconc.exe", NefconPin.ArchiveEntryPath);
        Assert.Equal("nefconc.exe", NefconPin.Executable);
    }
}