using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NoMistakes.Processes;

/// <summary>
/// Result of running a shell command: exit code plus captured stdout/stderr
/// (empty when not captured).
/// </summary>
public sealed record ShellCommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Thrown when a captured-output helper is asked to capture a stream the caller
/// already wired, mirroring Go's "exec: Stdout already set".
/// </summary>
public sealed class ShellCommandConfigurationException : Exception
{
    public ShellCommandConfigurationException(string message) : base(message) { }
}

/// <summary>
/// Runs a subprocess inside its own process group and reaps the whole group on
/// every exit path — clean exit, error, or cancellation. Ports Go's
/// <c>internal/shellenv</c> process lifecycle (<c>ConfigureShellCommand</c> /
/// <c>RunShellCommand</c> / <c>TerminateShellCommandGroup</c>).
///
/// Why a process group and not a parent-child tree walk: a test runner's worker
/// pool, a build watcher, or a dev server the child spawned can reparent to init
/// and outlive the leader. A tree walk (.NET's
/// <c>Process.Kill(entireProcessTree)</c>) follows live parent links and misses
/// those orphans; killing the whole process group catches them. On a clean exit
/// nothing else signals the group, so without this reap the orphans accumulate
/// across runs until the host OOMs and the OS SIGKILLs the daemon.
///
/// On Linux the child is launched via <c>setsid</c>, which execs the target in a
/// new session so the child leads its own process group (its PID is the group
/// id); the group is then killed with <c>kill(-pid, SIGKILL)</c>. On platforms
/// without POSIX process groups this degrades to a best-effort tree kill (see
/// <see cref="TerminateGroup"/>), a documented parity gap.
/// </summary>
public sealed class ShellCommand
{
    /// <summary>
    /// Worst-case ceiling for how long to wait, after the leader exits, for
    /// captured-output reads to finish before abandoning them. A grandchild that
    /// inherited and still holds the stdout/stderr pipe would otherwise wedge the
    /// read forever; this bounds it into a graceful failure. Mirrors Go's 5s
    /// <c>defaultWaitDelay</c>.
    /// </summary>
    public static readonly TimeSpan DefaultWaitDelay = TimeSpan.FromSeconds(5);

    private readonly ShellCommandSpec _spec;

    public ShellCommand(ShellCommandSpec spec)
    {
        _spec = spec;
    }

    /// <summary>
    /// Starts the command, waits for it, captures nothing (stdout/stderr inherit
    /// or are discarded per spec), and reaps the process group on exit or
    /// cancellation. Returns the exit code.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var result = await ExecuteAsync(captureStdout: false, captureStderr: false, ct).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>Runs the command capturing stdout only.</summary>
    public Task<ShellCommandResult> OutputAsync(CancellationToken ct = default) =>
        ExecuteAsync(captureStdout: true, captureStderr: false, ct);

    /// <summary>Runs the command capturing stdout and stderr together.</summary>
    public Task<ShellCommandResult> CombinedOutputAsync(CancellationToken ct = default) =>
        ExecuteAsync(captureStdout: true, captureStderr: true, ct);

    private async Task<ShellCommandResult> ExecuteAsync(bool captureStdout, bool captureStderr, CancellationToken ct)
    {
        var psi = BuildStartInfo(captureStdout, captureStderr);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read captured streams in the background so a slow/large writer never
        // deadlocks the pipe. On non-captured streams these tasks are no-ops.
        var stdoutTask = captureStdout ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stderrTask = captureStderr ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

        var waitDelay = _spec.WaitDelay ?? DefaultWaitDelay;

        try
        {
            using var reg = ct.Register(() => TerminateGroup(process));
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation already triggered the group kill via the registration.
            // Ensure the group is gone, then surface the cancellation.
            TerminateGroup(process);
            await DrainWithDelayAsync(stdoutTask, stderrTask, waitDelay).ConfigureAwait(false);
            throw;
        }

        // Clean or error exit: reap any survivors in the group (the leader is
        // already gone; this catches leaked descendants). Harmless no-op when
        // nothing survived.
        TerminateGroup(process);

        var (stdout, stderr) = await DrainWithDelayAsync(stdoutTask, stderrTask, waitDelay).ConfigureAwait(false);
        return new ShellCommandResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Waits for the capture tasks to finish, but no longer than
    /// <paramref name="waitDelay"/> after the leader exits — a grandchild holding
    /// the inherited pipe open must not wedge the read forever. On timeout the
    /// partial (possibly empty) content captured so far is returned.
    /// </summary>
    private static async Task<(string Stdout, string Stderr)> DrainWithDelayAsync(
        Task<string> stdoutTask, Task<string> stderrTask, TimeSpan waitDelay)
    {
        var both = Task.WhenAll(stdoutTask, stderrTask);
        var finished = await Task.WhenAny(both, Task.Delay(waitDelay)).ConfigureAwait(false);
        if (finished != both)
        {
            // Timed out: an inherited-pipe holder outlived the leader. Return what
            // completed; a task still running yields empty rather than blocking.
            return (
                stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty,
                stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty);
        }
        return (stdoutTask.Result, stderrTask.Result);
    }

    private ProcessStartInfo BuildStartInfo(bool captureStdout, bool captureStderr)
    {
        ProcessStartInfo psi;
        if (SupportsProcessGroups)
        {
            // setsid execs the target in a new session, so the child leads its
            // own process group (PID == PGID) and reparented descendants stay in
            // the group. setsid keeps the child's PID, so process.Id is the PGID.
            psi = new ProcessStartInfo { FileName = "setsid" };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(_spec.FileName);
            foreach (var arg in _spec.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else
        {
            psi = new ProcessStartInfo { FileName = _spec.FileName };
            foreach (var arg in _spec.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.RedirectStandardOutput = captureStdout;
        psi.RedirectStandardError = captureStderr;
        psi.UseShellExecute = false;

        if (!string.IsNullOrEmpty(_spec.WorkingDirectory))
        {
            psi.WorkingDirectory = _spec.WorkingDirectory;
        }
        if (_spec.Environment != null)
        {
            foreach (var kvp in _spec.Environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }
        return psi;
    }

    /// <summary>
    /// SIGKILLs the whole process group led by <paramref name="process"/>. Safe
    /// to call unconditionally after the leader exits: the group persists only
    /// while a member is alive, so a fully-exited command with no survivors is a
    /// harmless no-op (ESRCH). A null or already-disposed process is a no-op.
    ///
    /// On platforms without POSIX process groups this falls back to a best-effort
    /// <c>Process.Kill(entireProcessTree: true)</c>, which cannot catch orphans
    /// that reparented away from the leader (documented parity gap).
    /// </summary>
    public static void TerminateGroup(Process? process)
    {
        if (process is null)
        {
            return;
        }
        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            // Never started, or already reaped/disposed: nothing to signal.
            return;
        }

        if (SupportsProcessGroups)
        {
            // Negative pid targets the whole group; the leader's PID is the PGID.
            // ESRCH ("no such process/group") is the expected no-survivors case.
            _ = Native.Kill(-pid, Native.SIGKILL);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort on platforms without process groups.
        }
    }

    private static bool SupportsProcessGroups =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static class Native
    {
        public const int SIGKILL = 9;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        public static int Kill(int pid, int sig) => kill(pid, sig);
    }
}
