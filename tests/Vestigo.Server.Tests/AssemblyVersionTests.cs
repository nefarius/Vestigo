using System.Globalization;
using System.Reflection;

namespace Vestigo.Server.Tests;

public class AssemblyVersionTests
{
    [Fact]
    public void ServerAssemblyVersion_MatchesInformationalMajorMinorPatch()
    {
        Assembly assembly = typeof(Program).Assembly;
        Version? assemblyVersion = assembly.GetName().Version;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string numeric = informational.Split('+', 2)[0].Split('-', 2)[0];
        string[] parts = numeric.Split('.');
        Version expected = new(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0,
            parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0,
            0);

        Assert.Equal(expected, assemblyVersion);
    }
}
