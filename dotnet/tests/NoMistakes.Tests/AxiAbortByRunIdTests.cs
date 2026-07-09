using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Abort-by-id from outside a worktree: needs only NM_HOME (Paths) plus the
/// daemon. Ports Go internal/cli axi_test.go's runAxiAbortByRunID coverage:
/// success against a live run, and the idempotent no-op cases (unknown id,
/// inactive run, stopped daemon) that report aborted=false instead of failing.
/// </summary>
[Collection("daemon")]
public class AxiAbortByRunIdTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AbortByRunIdCancelsActiveRun()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });

        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        await started.Task.WaitAsync(Timeout);
        var done = manager.DoneTask(runId)!;

        var outcome = await AxiAbort.AbortByRunIdAsync(paths, runId).WaitAsync(Timeout);
        Assert.True(outcome.Aborted);
        Assert.Equal(runId, outcome.RunId);
        Assert.Null(outcome.Detail);

        await done.WaitAsync(Timeout);
        var stored = db.GetRun(runId)!;
        Assert.Equal(RunStatus.Cancelled, stored.Status);
        Assert.Equal(RunCancelReason.AbortedByUser, stored.Error);
    }

    [Fact]
    public async Task AbortByUnknownRunIdIsIdempotentNoOp()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);

        await using var host = await StartDaemonAsync(paths, db, manager);

        var outcome = await AxiAbort.AbortByRunIdAsync(paths, "no-such-run").WaitAsync(Timeout);
        Assert.False(outcome.Aborted);
        Assert.Equal("no-such-run", outcome.RunId);
        Assert.Equal("no active run with that id (no-op)", outcome.Detail);
    }

    [Fact]
    public async Task AbortByInactiveRunIdIsIdempotentNoOp()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        // Pipeline finishes immediately, so the run leaves the daemon's
        // active-run map before the abort arrives.
        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);

        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        var done = manager.DoneTask(runId);
        if (done != null)
        {
            await done.WaitAsync(Timeout);
        }

        var outcome = await AxiAbort.AbortByRunIdAsync(paths, runId).WaitAsync(Timeout);
        Assert.False(outcome.Aborted);
        Assert.Equal(runId, outcome.RunId);
        Assert.Equal("no active run with that id (no-op)", outcome.Detail);
    }

    [Fact]
    public async Task AbortWithStoppedDaemonIsIdempotentNoOp()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);

        var outcome = await AxiAbort.AbortByRunIdAsync(paths, "some-run-id").WaitAsync(Timeout);
        Assert.False(outcome.Aborted);
        Assert.Equal("some-run-id", outcome.RunId);
        Assert.Equal("daemon not running, so no active run to cancel (no-op)", outcome.Detail);
    }

    [Fact]
    public async Task AbortWithStaleSocketFileIsIdempotentNoOp()
    {
        // A crashed daemon can leave its socket file behind; dialing it fails,
        // which must read as "daemon not running", not an error.
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        paths.EnsureDirs();
        File.WriteAllText(paths.Socket, string.Empty);

        var outcome = await AxiAbort.AbortByRunIdAsync(paths, "some-run-id").WaitAsync(Timeout);
        Assert.False(outcome.Aborted);
        Assert.Equal("daemon not running, so no active run to cancel (no-op)", outcome.Detail);
    }

    private static async Task<DaemonHandle> StartDaemonAsync(Paths paths, Database db, RunManager manager)
    {
        var daemon = new DaemonHost(paths, db);
        RunIpcHandlers.Register(daemon.Server, manager, db);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);
        return new DaemonHandle { Daemon = daemon, Run = run };
    }

    private sealed class DaemonHandle : IAsyncDisposable
    {
        public required DaemonHost Daemon { get; init; }
        public required Task Run { get; init; }

        public async ValueTask DisposeAsync()
        {
            Daemon.Shutdown();
            await Run.WaitAsync(Timeout);
        }
    }
}
