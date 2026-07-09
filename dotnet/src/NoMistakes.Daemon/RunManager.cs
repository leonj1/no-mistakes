using System.Collections.Concurrent;
using NoMistakes.Core;
using NoMistakes.Data;

namespace NoMistakes.Daemon;

/// <summary>
/// Executes the pipeline for one run. The seam the slice-9 executor plugs
/// into: it owns the run's status transitions (running, completed, failed on
/// step failure) and must observe the token so cancellation stops the run.
/// Mirrors the executor.Execute call in Go's RunManager.startRun goroutine.
/// </summary>
public delegate Task PipelineRunner(Run run, Repo repo, CancellationToken cancellationToken);

/// <summary>
/// Tracks active pipeline runs and manages run lifecycle: creation with
/// per-repo+branch serialization, supersede-on-new-push cancellation,
/// user-requested cancellation, and shutdown. Ported from Go
/// internal/daemon/manager.go, reduced to run lifecycle — worktree setup,
/// trusted-config loading, agent creation, and event broadcast arrive with
/// their own slices.
/// </summary>
public sealed class RunManager
{
    private readonly Database db;
    private readonly PipelineRunner pipeline;
    private readonly object gate = new();
    private readonly Dictionary<string, ActiveRun> active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> branchLocks = new();
    private volatile bool shuttingDown;

    /// <summary>
    /// How long cancellation waits for a cancelled run's background task to
    /// finish (Go waits 30s in both cancelActiveRuns and Shutdown).
    /// </summary>
    internal TimeSpan CancelWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private sealed class ActiveRun
    {
        public required CancellationTokenSource Cts { get; init; }
        public required TaskCompletionSource Done { get; init; }

        /// <summary>
        /// The first cancellation cause requested for this run; becomes the
        /// run's error message (Go's context.WithCancelCause cause).
        /// </summary>
        public string? CancelReason { get; set; }
    }

    public RunManager(Database db, PipelineRunner pipeline)
    {
        this.db = db;
        this.pipeline = pipeline;
    }

    /// <summary>
    /// Creates a run record and launches pipeline execution in the background,
    /// cancelling any active run on the same repo+branch first (a new push
    /// supersedes the old run). Returns the new run's ID. Mirrors Go's
    /// startRun reduced to run lifecycle.
    /// </summary>
    public async Task<string> StartRunAsync(Repo repo, string branch, string headSha, string baseSha)
    {
        if (shuttingDown)
        {
            throw new InvalidOperationException("daemon is shutting down");
        }

        // Serialize per repo+branch so two concurrent pushes cannot both pass
        // the supersede check and create duplicate pipelines (Go branchLocks).
        var branchLock = branchLocks.GetOrAdd(repo.Id + "/" + branch, _ => new SemaphoreSlim(1, 1));
        await branchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelActiveRunsAsync(repo.Id, branch).ConfigureAwait(false);

            var run = db.InsertRun(repo.Id, branch, headSha, baseSha);
            var entry = new ActiveRun
            {
                Cts = new CancellationTokenSource(),
                Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            lock (gate)
            {
                active[run.Id] = entry;
            }
            _ = Task.Run(() => ExecuteRunAsync(run, repo, entry));
            return run.Id;
        }
        finally
        {
            branchLock.Release();
        }
    }

    /// <summary>
    /// Stops an active run and propagates cancellation to its pipeline.
    /// Throws when the run is not live in this manager (unknown, finished, or
    /// a different daemon's run), matching Go's "no active run" error; the
    /// idempotent no-op behavior for abort-by-id lives at the CLI layer.
    /// </summary>
    public void HandleCancel(string runId)
    {
        if (!TryCancel(runId, RunCancelReason.AbortedByUser, out _))
        {
            throw new InvalidOperationException($"no active run {runId}");
        }
    }

    /// <summary>
    /// Cancels all active runs and waits (bounded) for their background tasks,
    /// then refuses new runs. Called during daemon shutdown so no orphaned
    /// pipeline keeps mutating repos after the daemon exits.
    /// </summary>
    public void Shutdown()
    {
        shuttingDown = true;

        var toWait = new List<(CancellationTokenSource Cts, Task Done)>();
        lock (gate)
        {
            foreach (var entry in active.Values)
            {
                entry.CancelReason ??= RunCancelReason.DaemonShutdown;
                toWait.Add((entry.Cts, entry.Done.Task));
            }
        }
        foreach (var (cts, _) in toWait)
        {
            cts.Cancel();
        }
        if (toWait.Count > 0)
        {
            // Bounded wait like Go's 30s WaitGroup timeout; a wedged run is
            // abandoned rather than blocking shutdown forever.
            Task.WhenAll(toWait.Select(w => w.Done)).Wait(CancelWaitTimeout);
        }
    }

    /// <summary>The done task for an active run, or null when not active. Test hook.</summary>
    internal Task? DoneTask(string runId)
    {
        lock (gate)
        {
            return active.TryGetValue(runId, out var entry) ? entry.Done.Task : null;
        }
    }

    private bool TryCancel(string runId, string reason, out Task? done)
    {
        ActiveRun? entry;
        lock (gate)
        {
            if (!active.TryGetValue(runId, out entry))
            {
                done = null;
                return false;
            }
            entry.CancelReason ??= reason;
            done = entry.Done.Task;
        }
        entry.Cts.Cancel();
        return true;
    }

    /// <summary>
    /// Cancels in-progress runs for a repo+branch and waits (bounded) for
    /// their tasks so a superseding push never runs concurrently with the run
    /// it replaced. Mirrors Go's cancelActiveRuns.
    /// </summary>
    private async Task CancelActiveRunsAsync(string repoId, string branch)
    {
        var toWait = new List<Task>();
        foreach (var run in db.GetRunsByRepo(repoId))
        {
            if (run.Branch != branch)
            {
                continue;
            }
            if (run.Status != RunStatus.Pending && run.Status != RunStatus.Running)
            {
                continue;
            }
            if (TryCancel(run.Id, RunCancelReason.Superseded, out var done) && done != null)
            {
                toWait.Add(done);
            }
        }
        if (toWait.Count == 0)
        {
            return;
        }
        try
        {
            await Task.WhenAll(toWait).WaitAsync(CancelWaitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Go logs and proceeds; the branch lock still prevents duplicates.
        }
    }

    private async Task ExecuteRunAsync(Run run, Repo repo, ActiveRun entry)
    {
        try
        {
            await pipeline(run, repo, entry.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Cts.IsCancellationRequested)
        {
            MarkCancelled(run.Id, entry);
        }
        catch (Exception ex)
        {
            // Go's panic recovery: the run fails with the panic message rather
            // than staying pending/running forever.
            TrySetRunError(run.Id, $"internal panic: {ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                active.Remove(run.Id);
            }
            entry.Done.TrySetResult();
        }
    }

    /// <summary>
    /// Records the cancellation cause as the run's terminal state. Guarded on
    /// the run still being non-terminal so the slice-9 executor (which owns
    /// status transitions once it exists, like Go's executor writing
    /// context.Cause) is never overwritten.
    /// </summary>
    private void MarkCancelled(string runId, ActiveRun entry)
    {
        var reason = entry.CancelReason ?? RunCancelReason.AbortedByUser;
        try
        {
            var fresh = db.GetRun(runId);
            if (fresh is { Status: RunStatus.Pending or RunStatus.Running })
            {
                db.UpdateRunErrorStatus(runId, reason, RunStatus.Cancelled);
            }
        }
        catch (Exception)
        {
            // Best-effort, like Go logging a failed post-run DB update.
        }
    }

    private void TrySetRunError(string runId, string message)
    {
        try
        {
            db.UpdateRunErrorStatus(runId, message, RunStatus.Failed);
        }
        catch (Exception)
        {
            // Best-effort, like Go logging a failed post-panic DB update.
        }
    }
}
