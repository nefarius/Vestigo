using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.WindowsServices;
using Windows.Win32;

namespace Vestigo.Hosting;

/// <summary>A separate helper survives a graceful service shutdown and starts Vestigo again.</summary>
internal sealed class ServiceRestart(IHostApplicationLifetime lifetime)
{
    private const string Flag = "--restart-vestigo-service";
    private int _requested;
    public bool Available => WindowsServiceHelpers.IsWindowsService();
    public static bool IsInvocation(string[] args) => args.Length > 0 && args[0] == Flag;

    public Task RequestAsync() => RequestAsync(Available, () =>
    {
        ProcessStartInfo start = new(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add(Flag);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the restart helper.");
    });

    internal async Task RequestAsync(bool available, Func<Process> startHelper)
    {
        if (!available) throw new InvalidOperationException("Restart is available only when running as the installed Windows service.");
        if (Interlocked.CompareExchange(ref _requested, 1, 0) != 0)
            throw new InvalidOperationException("A restart has already been requested.");
        try
        {
            using Process helper = startHelper();
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                if (await helper.StandardOutput.ReadLineAsync(timeout.Token) != "READY")
                    throw new InvalidOperationException("The restart helper could not confirm service-start access.");
            }
            catch
            {
                try { if (!helper.HasExited) helper.Kill(); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                throw;
            }
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
            using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
            if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var startAccess = PInvoke.OpenService(manager, "Vestigo", PInvoke.SERVICE_START | PInvoke.SERVICE_QUERY_STATUS);
            if (startAccess.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using Process parent = Process.GetProcessById(pid);
            _ = parent.SafeHandle; // Open the wait handle before confirming readiness to the host.
            Console.Out.WriteLine("READY");
            Console.Out.Flush();
            if (!parent.WaitForExit(120_000)) return 3;
            using ServiceController service = new("Vestigo");
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException or ArgumentException)
        {
            // Do not boot the normal host or print configuration on a failed restart.
            return 1;
        }
    }
}
