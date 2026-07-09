using System.Diagnostics;
using System.Runtime.InteropServices;
using NoMistakes.Processes;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Process-lifecycle tests. They spawn real /bin/sh subprocesses and assert the
/// whole process group is reaped, mirroring the Go shellenv regressions. Linux
/// only (setsid + kill(-pgid)); skipped elsewhere.
/// </summary>
public class ShellCommandTests
{
    private static bool OnPosix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    [Fact]
    public async Task ReapsGrandchildAfterCleanExit()
    {
        if (!OnPosix)
        {
            return;
        }
        using var tmp = new TempDir();
        var pidFile = tmp.File("grandchild.pid");

        // Leader backgrounds a long-lived grandchild (stdio detached so it does
        // not hold captured pipes), records its pid, and exits 0.
        var script = $"( sleep 120 >/dev/null 2>&1 ) & echo $! > {pidFile}; exit 0";
        var spec = new ShellCommandSpec("/bin/sh", "-c", script);
        var cmd = new ShellCommand(spec);

        // OutputAsync waits, then TerminateGroup reaps the surviving grandchild.
        var result = await cmd.OutputAsync();
        Assert.Equal(0, result.ExitCode);

        var grandchild = ReadPid(pidFile, TimeSpan.FromSeconds(5));
        Assert.True(PidGoneWithin(grandchild, TimeSpan.FromSeconds(5)),
            $"grandchild {grandchild} still alive after clean-exit reap; group leaked");
    }

    [Fact]
    public async Task KillsGrandchildOnCancellation()
    {
        if (!OnPosix)
        {
            return;
        }
        using var tmp = new TempDir();
        var pidFile = tmp.File("grandchild.pid");

        // Leader records a long-lived grandchild pid then sleeps, so the command
        // is still running when we cancel.
        var script = $"( sleep 120 >/dev/null 2>&1 ) & echo $! > {pidFile}; sleep 120";
        var spec = new ShellCommandSpec("/bin/sh", "-c", script);
        var cmd = new ShellCommand(spec);

        using var cts = new CancellationTokenSource();
        var run = cmd.RunAsync(cts.Token);

        var grandchild = ReadPid(pidFile, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(PidGoneWithin(grandchild, TimeSpan.FromSeconds(5)),
            $"grandchild {grandchild} still alive after cancellation; group leaked");
    }

    [Fact]
    public async Task CombinedOutput_ReturnsCleanExitWithInheritedPipeGrandchild()
    {
        if (!OnPosix)
        {
            return;
        }
        // Leader prints, then leaves a grandchild that inherits and holds the
        // stdout pipe open. The command must still return promptly with the
        // leader's output rather than blocking on the pipe holder.
        var spec = new ShellCommandSpec("/bin/sh", "-c", "printf 'leader done\\n'; sleep 30 & exit 0")
        {
            WaitDelay = TimeSpan.FromMilliseconds(200),
        };
        var cmd = new ShellCommand(spec);

        var sw = Stopwatch.StartNew();
        var result = await cmd.CombinedOutputAsync();
        sw.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("leader done", result.Stdout);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"CombinedOutput blocked {sw.Elapsed} on an inherited-pipe holder");
    }

    [Fact]
    public void TerminateGroup_NoopOnNull()
    {
        // Must not throw on a null process.
        ShellCommand.TerminateGroup(null);
    }

    [Fact]
    public async Task Output_CapturesStdout()
    {
        if (!OnPosix)
        {
            return;
        }
        var spec = new ShellCommandSpec("/bin/sh", "-c", "printf hello");
        var result = await new ShellCommand(spec).OutputAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Stdout);
    }

    [Fact]
    public async Task Run_PropagatesNonZeroExit()
    {
        if (!OnPosix)
        {
            return;
        }
        var spec = new ShellCommandSpec("/bin/sh", "-c", "exit 3");
        var code = await new ShellCommand(spec).RunAsync();
        Assert.Equal(3, code);
    }

    // --- helpers --------------------------------------------------------------

    private static int ReadPid(string pidFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (System.IO.File.Exists(pidFile))
            {
                var text = System.IO.File.ReadAllText(pidFile).Trim();
                if (int.TryParse(text, out var pid) && pid > 0)
                {
                    return pid;
                }
            }
            Thread.Sleep(25);
        }
        throw new TimeoutException($"pid file {pidFile} never populated");
    }

    private static bool PidGoneWithin(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!PidAlive(pid))
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return !PidAlive(pid);
    }

    private static bool PidAlive(int pid)
    {
        // kill(pid, 0) probes existence without signalling: 0 = alive, -1/ESRCH = gone.
        return Kill(pid, 0) == 0;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static int Kill(int pid, int sig) => kill(pid, sig);
}
