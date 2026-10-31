using Vestigo.RemoteDebugger;

namespace Vestigo.Server.Tests;

public class CliRunnerTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Run_CapturesStdoutStderrAndExitCode()
    {
        CliOutput? result = CliRunner.Run(Cmd, ["/c", "echo out& echo err 1>&2& exit 3"], TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardError.Trim());
    }

    [Fact]
    public void Run_ReturnsNullAndStopsProcessOnTimeout()
    {
        DateTime started = DateTime.UtcNow;

        CliOutput? result = CliRunner.Run(Cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(500));

        Assert.Null(result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Run_ThrowsWin32ExceptionWhenExecutableMissing()
    {
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            CliRunner.Run(Path.Combine(Path.GetTempPath(), "vestigo-missing-" + Guid.NewGuid().ToString("N") + ".exe"), [], TimeSpan.FromSeconds(5)));
    }
}