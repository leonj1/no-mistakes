using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Run-manager lifecycle: run creation, cancellation, supersede-on-new-push,
/// and shutdown. Ported from Go internal/daemon/manager.go behavior, with the
/// pipeline executor replaced by the PipelineRunner seam (the real executor
/// arrives in slice 9).
/// </summary>
public class RunManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartRunCreatesPendingRunAndInvokesPipeline()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var invoked = new TaskCompletionSource<(string RunId, string RepoId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, (run, r, _) =>
        {
            invoked.TrySetResult((run.Id, r.Id));
            return Task.CompletedTask;
        });

        var runId = await manager.StartRunAsync(repo, "feature", "abc123", "def456").WaitAsync(Timeout);
        Assert.NotEmpty(runId);

        var stored = db.GetRun(runId);
        Assert.NotNull(stored);
        Assert.Equal(repo.Id, stored!.RepoId);
        Assert.Equal("feature", stored.Branch);
        Assert.Equal("abc123", stored.HeadSha);
        Assert.Equal("def456", stored.BaseSha);
        Assert.Equal(RunStatus.Pending, stored.Status);

        var got = await invoked.Task.WaitAsync(Timeout);
        Assert.Equal(runId, got.RunId);
        Assert.Equal(repo.Id, got.RepoId);
    }

    [Fact]
    public async Task HandleCancelMarksRunCancelledWithAbortReason()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });

        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        await started.Task.WaitAsync(Timeout);
        var done = manager.DoneTask(runId);
        Assert.NotNull(done);

        manager.HandleCancel(runId);
        await done!.WaitAsync(Timeout);

        var stored = db.GetRun(runId)!;
        Assert.Equal(RunStatus.Cancelled, stored.Status);
        Assert.Equal(RunCancelReason.AbortedByUser, stored.Error);
    }

    [Fact]
    public async Task HandleCancelUnknownOrFinishedRunThrows()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);

        var ex = Assert.Throws<InvalidOperationException>(() => manager.HandleCancel("nope"));
        Assert.Equal("no active run nope", ex.Message);

        // A finished run is no longer active either.
        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        var done = manager.DoneTask(runId);
        if (done != null)
        {
            await done.WaitAsync(Timeout);
        }
        Assert.Throws<InvalidOperationException>(() => manager.HandleCancel(runId));
    }

    [Fact]
    public async Task NewRunSupersedesActiveRunOnSameBranch()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, async (run, _, ct) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Running);
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });

        var firstId = await manager.StartRunAsync(repo, "feature", "aaa", "base").WaitAsync(Timeout);
        await started.Task.WaitAsync(Timeout);

        var secondId = await manager.StartRunAsync(repo, "feature", "bbb", "base").WaitAsync(Timeout);
        Assert.NotEqual(firstId, secondId);

        var first = db.GetRun(firstId)!;
        Assert.Equal(RunStatus.Cancelled, first.Status);
        Assert.Equal(RunCancelReason.Superseded, first.Error);
    }

    [Fact]
    public async Task RunsOnDifferentBranchesStayActiveConcurrently()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var manager = new RunManager(db, async (_, _, ct) =>
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct));

        var firstId = await manager.StartRunAsync(repo, "feature-a", "aaa", "base").WaitAsync(Timeout);
        var secondId = await manager.StartRunAsync(repo, "feature-b", "bbb", "base").WaitAsync(Timeout);

        // Neither run cancelled the other: both still live in the manager.
        Assert.NotNull(manager.DoneTask(firstId));
        Assert.NotNull(manager.DoneTask(secondId));
        Assert.Equal(RunStatus.Pending, db.GetRun(firstId)!.Status);

        var firstDone = manager.DoneTask(firstId)!;
        var secondDone = manager.DoneTask(secondId)!;
        manager.HandleCancel(firstId);
        manager.HandleCancel(secondId);
        await Task.WhenAll(firstDone, secondDone).WaitAsync(Timeout);
    }

    [Fact]
    public async Task StartRunDuringShutdownIsRefused()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);
        manager.Shutdown();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartRunAsync(repo, "feature", "abc", "def"));
        Assert.Equal("daemon is shutting down", ex.Message);
    }

    [Fact]
    public async Task ShutdownCancelsActiveRunsWithShutdownReason()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });

        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        await started.Task.WaitAsync(Timeout);

        manager.Shutdown();

        var stored = db.GetRun(runId)!;
        Assert.Equal(RunStatus.Cancelled, stored.Status);
        Assert.Equal(RunCancelReason.DaemonShutdown, stored.Error);
    }

    [Fact]
    public async Task PipelineExceptionMarksRunFailed()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var manager = new RunManager(db, (_, _, _) => throw new InvalidOperationException("boom"));

        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        var done = manager.DoneTask(runId);
        if (done != null)
        {
            await done.WaitAsync(Timeout);
        }
        else
        {
            // The pipeline task may already have finished and untracked itself.
            await WaitForTerminalAsync(db, runId);
        }

        var stored = db.GetRun(runId)!;
        Assert.Equal(RunStatus.Failed, stored.Status);
        Assert.Equal("internal panic: boom", stored.Error);
    }

    [Fact]
    public async Task PipelineOwnsTerminalStatusOnCleanCompletion()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var manager = new RunManager(db, (run, _, _) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Completed);
            return Task.CompletedTask;
        });

        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        var done = manager.DoneTask(runId);
        if (done != null)
        {
            await done.WaitAsync(Timeout);
        }
        else
        {
            await WaitForTerminalAsync(db, runId);
        }

        // The manager must not second-guess the pipeline's own terminal state.
        Assert.Equal(RunStatus.Completed, db.GetRun(runId)!.Status);
        Assert.Null(db.GetRun(runId)!.Error);
    }

    private static async Task WaitForTerminalAsync(Database db, string runId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = db.GetRun(runId)!.Status;
            if (status != RunStatus.Pending && status != RunStatus.Running)
            {
                return;
            }
            await Task.Delay(10);
        }
    }
}
