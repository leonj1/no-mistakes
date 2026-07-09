using NoMistakes.Config;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Pipeline;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the acceptance scenarios from Go's internal/pipeline executor tests:
/// success, failure, skipped steps (configured and outcome), approval gates,
/// the auto-fix loop and its per-step limit, round persistence, the user-fix
/// re-execution path, terminal run states, and cancellation propagation.
/// </summary>
public class ExecutorTests
{
    // A configurable fake step whose Execute delegate the test drives round by
    // round, mirroring Go's fakeStep used across the executor test suite.
    private sealed class FakeStep : IStep
    {
        private readonly Func<StepContext, int, StepOutcome> onExecute;
        public int Calls { get; private set; }
        public List<string> PreviousFindingsSeen { get; } = new();

        public FakeStep(string name, Func<StepContext, int, StepOutcome> onExecute)
        {
            Name = name;
            this.onExecute = onExecute;
        }

        public string Name { get; }

        public Task<StepOutcome> ExecuteAsync(StepContext sctx)
        {
            PreviousFindingsSeen.Add(sctx.PreviousFindings);
            var outcome = onExecute(sctx, Calls);
            Calls++;
            return Task.FromResult(outcome);
        }
    }

    private static (Database Db, Paths Paths, Run Run, Repo Repo, string WorkDir) NewHarness(TempDir dir)
    {
        var db = DataTestSupport.OpenTestDb(dir);
        var paths = Paths.WithRoot(dir.Path);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");
        var workDir = dir.File("work");
        Directory.CreateDirectory(workDir);
        return (db, paths, run, repo, workDir);
    }

    private static string Findings(string action, string severity = "high") =>
        $"{{\"findings\":[{{\"severity\":\"{severity}\",\"description\":\"d\",\"action\":\"{action}\"}}],\"summary\":\"s\",\"risk_level\":\"\",\"risk_rationale\":\"\"}}";

    [Fact]
    public async Task SuccessfulRun_MarksStepsAndRunCompleted()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review, (_, _) => new StepOutcome());
        var exec = new Executor(db, paths, null, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Completed, db.GetRun(run.Id)!.Status);
        var steps = db.GetStepsByRun(run.Id);
        Assert.Equal(StepStatus.Completed, Assert.Single(steps).Status);
    }

    [Fact]
    public async Task EmptySteps_CompletesRun()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var exec = new Executor(db, paths, null, null, Array.Empty<IStep>());
        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Completed, db.GetRun(run.Id)!.Status);
    }

    [Fact]
    public async Task StepError_FailsRunAndStep()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review, (_, _) => throw new InvalidOperationException("boom"));
        var exec = new Executor(db, paths, null, null, new[] { step });

        await Assert.ThrowsAsync<StepFailedException>(
            () => exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken));

        var stored = db.GetRun(run.Id)!;
        Assert.Equal(RunStatus.Failed, stored.Status);
        Assert.Contains("boom", stored.Error);
        Assert.Equal(StepStatus.Failed, Assert.Single(db.GetStepsByRun(run.Id)).Status);
    }

    [Fact]
    public async Task ConfiguredSkip_DoesNotExecuteAndContinues()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var review = new FakeStep(StepName.Review, (_, _) => new StepOutcome());
        var test = new FakeStep(StepName.Test, (_, _) => new StepOutcome());
        var exec = new Executor(db, paths, null, null, new[] { review, test });
        exec.SetSkippedSteps(new[] { StepName.Review });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(0, review.Calls);
        Assert.Equal(1, test.Calls);
        var steps = db.GetStepsByRun(run.Id);
        Assert.Equal(StepStatus.Skipped, steps.Single(s => s.StepName == StepName.Review).Status);
        Assert.Equal(StepStatus.Completed, steps.Single(s => s.StepName == StepName.Test).Status);
    }

    [Fact]
    public async Task SkipRemaining_MarksSubsequentStepsSkipped()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var review = new FakeStep(StepName.Review, (_, _) => new StepOutcome { SkipRemaining = true });
        var test = new FakeStep(StepName.Test, (_, _) => new StepOutcome());
        var exec = new Executor(db, paths, null, null, new[] { review, test });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(0, test.Calls);
        var steps = db.GetStepsByRun(run.Id);
        Assert.Equal(StepStatus.Completed, steps.Single(s => s.StepName == StepName.Review).Status);
        Assert.Equal(StepStatus.Skipped, steps.Single(s => s.StepName == StepName.Test).Status);
    }

    [Fact]
    public async Task AutoFix_TriggersUntilClean_WithinLimit()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        // Round 0 reports an auto-fixable finding; round 1 is clean.
        var step = new FakeStep(StepName.Lint, (_, call) => call == 0
            ? new StepOutcome { AutoFixable = true, Findings = Findings(FindingActions.AutoFix) }
            : new StepOutcome());
        var config = ConfigWithAutoFix(StepName.Lint, 2);
        var exec = new Executor(db, paths, config, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(2, step.Calls);
        Assert.Equal(StepStatus.Completed, Assert.Single(db.GetStepsByRun(run.Id)).Status);
        // The fix re-execution saw the previous auto-fixable findings.
        Assert.NotEqual(string.Empty, step.PreviousFindingsSeen[1]);
    }

    [Fact]
    public async Task AutoFix_RespectsMaxAttempts_ThenAcceptsRemainingInfoFindings()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        // Always reports an auto-fixable finding; limit 1 means exactly one fix
        // attempt, then the step completes (no NeedsApproval, no ask-user).
        var step = new FakeStep(StepName.Lint,
            (_, _) => new StepOutcome { AutoFixable = true, Findings = Findings(FindingActions.AutoFix) });
        var config = ConfigWithAutoFix(StepName.Lint, 1);
        var exec = new Executor(db, paths, config, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(2, step.Calls); // initial + 1 fix attempt
        Assert.Equal(StepStatus.Completed, Assert.Single(db.GetStepsByRun(run.Id)).Status);
    }

    [Fact]
    public async Task AutoFix_DisabledWithZero_DoesNotFix()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review,
            (_, _) => new StepOutcome { AutoFixable = true, Findings = Findings(FindingActions.AutoFix) });
        var config = ConfigWithAutoFix(StepName.Review, 0);
        var exec = new Executor(db, paths, config, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(1, step.Calls);
    }

    [Fact]
    public async Task AskUserFinding_ParksEvenWithoutNeedsApprovalFlag()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review,
            (_, _) => new StepOutcome { Findings = Findings(FindingActions.AskUser) });
        var exec = new Executor(db, paths, null, null, new[] { step });

        var runTask = exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);
        await WaitForStatus(db, run.Id, StepName.Review, StepStatus.AwaitingApproval);
        Assert.NotNull(db.GetRun(run.Id)!.AwaitingAgentSince);

        exec.Respond(StepName.Review, ApprovalAction.Approve);
        await runTask;

        Assert.Equal(RunStatus.Completed, db.GetRun(run.Id)!.Status);
        Assert.Null(db.GetRun(run.Id)!.AwaitingAgentSince);
    }

    [Fact]
    public async Task ApprovalSkip_MarksStepSkipped_RunCompletes()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review,
            (_, _) => new StepOutcome { NeedsApproval = true, Findings = Findings(FindingActions.AskUser) });
        var exec = new Executor(db, paths, null, null, new[] { step });

        var runTask = exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);
        await WaitForStatus(db, run.Id, StepName.Review, StepStatus.AwaitingApproval);

        exec.Respond(StepName.Review, ApprovalAction.Skip);
        await runTask;

        Assert.Equal(StepStatus.Skipped, Assert.Single(db.GetStepsByRun(run.Id)).Status);
        Assert.Equal(RunStatus.Completed, db.GetRun(run.Id)!.Status);
    }

    [Fact]
    public async Task ApprovalFix_ReExecutesAndPersistsFixReviewRound()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        // Round 0 parks for approval; the fix re-run is clean.
        var step = new FakeStep(StepName.Review, (_, call) => call == 0
            ? new StepOutcome { NeedsApproval = true, Findings = Findings(FindingActions.AskUser) }
            : new StepOutcome());
        var exec = new Executor(db, paths, null, null, new[] { step });

        var runTask = exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);
        await WaitForStatus(db, run.Id, StepName.Review, StepStatus.AwaitingApproval);

        exec.Respond(StepName.Review, ApprovalAction.Fix);
        await runTask;

        Assert.Equal(2, step.Calls);
        Assert.Equal(StepStatus.Completed, Assert.Single(db.GetStepsByRun(run.Id)).Status);
        // The fix loop saw the selected previous findings on the re-run.
        Assert.NotEqual(string.Empty, step.PreviousFindingsSeen[1]);
        var sr = db.GetStepsByRun(run.Id).Single();
        var rounds = db.GetRoundsByStep(sr.Id);
        Assert.Equal(2, rounds.Count);
        Assert.Equal("auto_fix", rounds[1].Trigger); // fix round persisted as auto_fix
    }

    [Fact]
    public async Task Cancellation_FailsRunWhileParked()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;
        using var cts = new CancellationTokenSource();

        var step = new FakeStep(StepName.Review,
            (_, _) => new StepOutcome { NeedsApproval = true, Findings = Findings(FindingActions.AskUser) });
        var exec = new Executor(db, paths, null, null, new[] { step });

        var runTask = exec.ExecuteAsync(run, repo, work, cts.Token);
        await WaitForStatus(db, run.Id, StepName.Review, StepStatus.AwaitingApproval);

        cts.Cancel();
        await Assert.ThrowsAsync<StepFailedException>(() => runTask);

        // Terminal state: run failed, parked marker cleared, step failed.
        var stored = db.GetRun(run.Id)!;
        Assert.Equal(RunStatus.Failed, stored.Status);
        Assert.Null(stored.AwaitingAgentSince);
        Assert.Equal(StepStatus.Failed, Assert.Single(db.GetStepsByRun(run.Id)).Status);
    }

    [Fact]
    public async Task PrUrlOutcome_PropagatesToRun()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Pr, (_, _) => new StepOutcome { PrUrl = "https://example/pr/1" });
        var exec = new Executor(db, paths, null, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        // The executor propagates the outcome URL to the in-memory run object and
        // emits a run_updated event (matching Go); the PR step itself owns
        // persisting it to the DB.
        Assert.Equal("https://example/pr/1", run.PrUrl);
    }

    [Fact]
    public async Task DurationOverride_ReplacesReportedDuration()
    {
        using var dir = new TempDir();
        var (db, paths, run, repo, work) = NewHarness(dir);
        using var _ = db;

        var step = new FakeStep(StepName.Review, (_, _) => new StepOutcome { DurationOverrideMs = 4200 });
        var exec = new Executor(db, paths, null, null, new[] { step });

        await exec.ExecuteAsync(run, repo, work, TestContext.Current.CancellationToken);

        Assert.Equal(4200, Assert.Single(db.GetStepsByRun(run.Id)).DurationMs);
    }

    // Builds a config whose auto-fix limit for one step is set, leaving others 0.
    private static Config.Config ConfigWithAutoFix(string step, int limit)
    {
        var af = new AutoFix();
        switch (step)
        {
            case StepName.Lint: af.Lint = limit; break;
            case StepName.Test: af.Test = limit; break;
            case StepName.Review: af.Review = limit; break;
            case StepName.Document: af.Document = limit; break;
            case StepName.Ci: af.Ci = limit; break;
            case StepName.Rebase: af.Rebase = limit; break;
        }
        return new Config.Config { AutoFix = af };
    }

    private static async Task WaitForStatus(Database db, string runId, string stepName, string status)
    {
        for (var i = 0; i < 200; i++)
        {
            var step = db.GetStepsByRun(runId).FirstOrDefault(s => s.StepName == stepName);
            if (step != null && step.Status == status)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"step {stepName} did not reach {status}");
    }
}
