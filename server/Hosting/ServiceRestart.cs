using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Vestigo.Hosting;

/// <summary>A separate helper survives a graceful service shutdown and starts Vestigo again.</summary>
internal sealed class ServiceRestart(IHostApplicationLifetime lifetime)
{
    private const string Flag = "--restart-vestigo-service";
    private int _requested;
    public bool Available => WindowsServiceHelpers.IsWindowsService();
    public static bool IsInvocation(string[] args) => args.Length > 0 && args[0] == Flag;

    public void Request()
    {
        if (!Available) throw new InvalidOperationException("Restart is available only when running as the installed Windows service.");
        if (Interlocked.CompareExchange(ref _requested, 1, 0) != 0)
            throw new InvalidOperationException("A restart has already been requested.");
        try
        {
            ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Flag);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using Process helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start the restart helper.");
            // Allow the UI event result to reach the browser before closing circuits and active jobs.
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(2)); lifetime.StopApplication(); });
        }
        catch
        {
            Interlocked.Exchange(ref _requested, 0);
            throw;
        }
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int pid) || pid <= 0) return 2;
        try
        {
            try
            {
                using Process parent = Process.GetProcessById(pid);
                if (!parent.WaitForExit(120_000)) return 3;
            }
            catch (ArgumentException) { /* The service already exited. */ }
            using ServiceController service = new("Vestigo");
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            // Do not boot the normal host or print configuration on a failed restart.
            return 1;
        }
    }
}
