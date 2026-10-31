using System.Diagnostics;

using Vestigo.Devices;

namespace Vestigo.Server.Tests;

/// <summary>Exercises the real CliWrap-backed <see cref="NefconProcess" /> with cmd.exe and ping.exe.</summary>
public sealed class NefconProcessTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly NefconProcess _process = new();

    [Fact]
    public async Task ReturnsExitCodeWithoutThrowingOnNonZero()
    {
        NefconProcessOutput result = await _process.RunAsync(Cmd, ["/c", "exit 3010"], Generous, CancellationToken.None);

        Assert.Equal(3010, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task CapturesStdoutAndStderr()
    {
        NefconProcessOutput result = await _process.RunAsync(
            Cmd, ["/c", "echo to-out& echo to-err 1>&2"], Generous, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("to-out", result.Output);
        Assert.Contains("to-err", result.Output);
    }

    [Fact]
    public async Task KeepsOnlyTheOutputTail()
    {
        NefconProcessOutput result = await _process.RunAsync(
            Cmd,
            ["/c", "for /L %i in (1,1,3000) do @echo 0123456789012345678901234567890123456789"],
            Generous,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
        Assert.True(result.Output.Length <= NefconProcess.MaxOutputChars);
    }

    [Fact]
    public async Task TimeoutKillsProcessTreeAndReportsTimedOut()
    {
        Stopwatch watch = Stopwatch.StartNew();
        NefconProcessOutput result = await _process.RunAsync(
            Cmd, ["/c", "ping -n 60 127.0.0.1 > nul"], TimeSpan.FromMilliseconds(500), CancellationToken.None);
        watch.Stop();

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task CallerCancellationThrows()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));
        Stopwatch watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _process.RunAsync(Ping, ["-n", "60", "127.0.0.1"], Generous, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task PassesArgumentsWithoutShellInterpretation()
    {
        // ping validates its own arguments; a literal '&' must not spawn a shell command.
        NefconProcessOutput result = await _process.RunAsync(
            Ping, ["-n", "1", "127.0.0.1&echo injected"], Generous, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("injected" + Environment.NewLine, result.Output + Environment.NewLine, StringComparison.Ordinal);
    }
}