using System.Security.Principal;

using Vestigo.Configuration;
using Vestigo.Etws;

namespace Vestigo.Server.Tests;

public class WindowsElevatedTests
{
    [WindowsElevatedFact]
    [Trait("Category", "WindowsElevated")]
    public async Task RealtimeFactory_StartsAndStopsASession()
    {
        EtwRealtimeTraceSessionFactory factory = new();
        string sessionName = CaptureManager.SessionPrefix + Guid.NewGuid().ToString("N")[..12];
        await using IRealtimeTraceSession session = factory.Create(sessionName, new VestigoOptions());
        session.EnableProvider(Guid.Parse("11111111-1111-1111-1111-111111111111"), TraceLevelSelection.Verbose, ulong.MaxValue, 0);

        Assert.Equal(sessionName, session.SessionName);
        Assert.Contains(sessionName, factory.ListSessions());
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
