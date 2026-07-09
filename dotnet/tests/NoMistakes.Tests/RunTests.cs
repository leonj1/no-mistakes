using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ported from Go's internal/db/run_test.go: run CRUD, the awaiting-agent signal,
/// active-run queries, cascade delete, and stale-run recovery.
/// </summary>
public sealed class RunTests
{
    [Fact]
    public void RunInsertAndGet()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");
        Assert.NotEqual(string.Empty, run.Id);
        Assert.Equal(RunStatus.Pending, run.Status);

        var got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.Equal("feature", got!.Branch);
        Assert.Equal("abc123", got.HeadSha);
    }

    [Fact]
    public void RunAwaitingAgentSetAndClear()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");

        Assert.Null(run.AwaitingAgentSince);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.SetRunAwaitingAgent(run.Id);
        var got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.NotNull(got!.AwaitingAgentSince);
        Assert.True(got.AwaitingAgentSince!.Value >= before);

        db.ClearRunAwaitingAgent(run.Id);
        got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.Null(got!.AwaitingAgentSince);
    }

    [Fact]
    public void RecoverStaleRunsClearsAwaitingAgent()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");
        db.UpdateRunStatus(run.Id, RunStatus.Running);
        db.SetRunAwaitingAgent(run.Id);

        db.RecoverStaleRuns("daemon restarted");

        var got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.Equal(RunStatus.Failed, got!.Status);
        Assert.Null(got.AwaitingAgentSince);
    }

    [Fact]
    public void RunGetNotFound()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        Assert.Null(db.GetRun("nonexistent"));
    }

    [Fact]
    public void RunsByRepo()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        db.InsertRun(repo.Id, "feature-1", "aaa", "bbb");
        db.InsertRun(repo.Id, "feature-2", "ccc", "ddd");

        var runs = db.GetRunsByRepo(repo.Id);
        Assert.Equal(2, runs.Count);
        Assert.Equal("feature-2", runs[0].Branch); // newest first
    }

    [Fact]
    public void ActiveRun()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        Assert.Null(db.GetActiveRun(repo.Id, ""));

        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var active = db.GetActiveRun(repo.Id, "");
        Assert.NotNull(active);
        Assert.Equal(run.Id, active!.Id);

        db.UpdateRunStatus(run.Id, RunStatus.Completed);
        Assert.Null(db.GetActiveRun(repo.Id, ""));
    }

    [Fact]
    public void ActiveRunStrictBranchMatch()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/branchpref", "git@github.com:user/branchpref.git", "main");

        var runA = db.InsertRun(repo.Id, "feature-a", "aaa", "000");
        var runB = db.InsertRun(repo.Id, "feature-b", "bbb", "000");

        var active = db.GetActiveRun(repo.Id, "");
        Assert.NotNull(active);
        Assert.Equal(runB.Id, active!.Id);

        active = db.GetActiveRun(repo.Id, "feature-a");
        Assert.NotNull(active);
        Assert.Equal(runA.Id, active!.Id);

        active = db.GetActiveRun(repo.Id, "feature-b");
        Assert.NotNull(active);
        Assert.Equal(runB.Id, active!.Id);

        Assert.Null(db.GetActiveRun(repo.Id, "feature-c"));
    }

    [Fact]
    public void ActiveRunsAcrossRepos()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repoA = db.InsertRepo("/home/user/project-a", "git@github.com:user/project-a.git", "main");
        var repoB = db.InsertRepo("/home/user/project-b", "git@github.com:user/project-b.git", "main");

        var pendingRun = db.InsertRun(repoA.Id, "feature-a", "aaa", "000");
        var runningRun = db.InsertRun(repoB.Id, "feature-b", "bbb", "000");
        db.UpdateRunStatus(runningRun.Id, RunStatus.Running);
        var completedRun = db.InsertRun(repoA.Id, "done", "ccc", "000");
        db.UpdateRunStatus(completedRun.Id, RunStatus.Completed);
        var failedRun = db.InsertRun(repoB.Id, "failed", "ddd", "000");
        db.UpdateRunStatus(failedRun.Id, RunStatus.Failed);
        var cancelledRun = db.InsertRun(repoB.Id, "cancelled", "eee", "000");
        db.UpdateRunStatus(cancelledRun.Id, RunStatus.Cancelled);

        var runs = db.GetActiveRuns();
        Assert.Equal(2, runs.Count);

        var got = runs.ToDictionary(r => r.Id, r => r.Status);
        Assert.Equal(RunStatus.Pending, got[pendingRun.Id]);
        Assert.Equal(RunStatus.Running, got[runningRun.Id]);
        Assert.False(got.ContainsKey(completedRun.Id));
        Assert.False(got.ContainsKey(failedRun.Id));
        Assert.False(got.ContainsKey(cancelledRun.Id));
    }

    [Fact]
    public void UpdateRunStatus()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        db.UpdateRunStatus(run.Id, RunStatus.Running);
        Assert.Equal(RunStatus.Running, db.GetRun(run.Id)!.Status);
    }

    [Fact]
    public void UpdateRunPrUrl()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        const string prUrl = "https://github.com/user/project/pull/1";
        db.UpdateRunPrUrl(run.Id, prUrl);
        Assert.Equal(prUrl, db.GetRun(run.Id)!.PrUrl);
    }

    [Fact]
    public void UpdateRunHeadSha()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        db.UpdateRunHeadSha(run.Id, "xyz");
        Assert.Equal("xyz", db.GetRun(run.Id)!.HeadSha);
    }

    [Fact]
    public void UpdateRunError()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        db.UpdateRunError(run.Id, "something broke");
        var got = db.GetRun(run.Id);
        Assert.Equal("something broke", got!.Error);
        Assert.Equal(RunStatus.Failed, got.Status);
    }

    [Fact]
    public void UpdateRunIntentRoundTrip()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/intent", "git@github.com:user/intent.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        var got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.Null(got!.Intent);
        Assert.Null(got.IntentSource);
        Assert.Null(got.IntentSessionId);
        Assert.Null(got.IntentScore);

        db.UpdateRunIntent(run.Id, new RunIntent("user wanted to add foo", "claude", "abc-123", 0.85));

        got = db.GetRun(run.Id);
        Assert.NotNull(got);
        Assert.Equal("user wanted to add foo", got!.Intent);
        Assert.Equal("claude", got.IntentSource);
        Assert.Equal("abc-123", got.IntentSessionId);
        Assert.Equal(0.85, got.IntentScore);
    }

    [Fact]
    public void CascadeDeleteRepo()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.DeleteRepo(repo.Id);
        Assert.Null(db.GetRun(run.Id));
        Assert.Null(db.GetStepResult(step.Id));
    }

    [Fact]
    public void RecoverStaleRunsMarksRunsFailed()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var pendingRun = db.InsertRun(repo.Id, "feat-a", "aaa", "bbb");
        var runningRun = db.InsertRun(repo.Id, "feat-b", "ccc", "ddd");
        db.UpdateRunStatus(runningRun.Id, RunStatus.Running);
        var completedRun = db.InsertRun(repo.Id, "feat-c", "eee", "fff");
        db.UpdateRunStatus(completedRun.Id, RunStatus.Completed);

        var count = db.RecoverStaleRuns("daemon crashed");
        Assert.Equal(2, count);

        var got = db.GetRun(pendingRun.Id);
        Assert.Equal(RunStatus.Failed, got!.Status);
        Assert.Equal("daemon crashed", got.Error);

        Assert.Equal(RunStatus.Failed, db.GetRun(runningRun.Id)!.Status);
        Assert.Equal(RunStatus.Completed, db.GetRun(completedRun.Id)!.Status);
    }

    [Fact]
    public void RecoverStaleRunsMarksStepsFailed()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project2", "git@github.com:user/project2.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        var runningStep = db.InsertStepResult(run.Id, StepName.Review);
        db.StartStep(runningStep.Id);
        var awaitingStep = db.InsertStepResult(run.Id, StepName.Test);
        db.UpdateStepStatus(awaitingStep.Id, StepStatus.AwaitingApproval);
        var fixingStep = db.InsertStepResult(run.Id, StepName.Lint);
        db.UpdateStepStatus(fixingStep.Id, StepStatus.Fixing);
        var completedStep = db.InsertStepResult(run.Id, StepName.Push);
        db.CompleteStep(completedStep.Id, 0, 100, "/tmp/log");
        var pendingStep = db.InsertStepResult(run.Id, StepName.Pr);

        db.RecoverStaleRuns("daemon crashed");

        Assert.Equal(StepStatus.Failed, db.GetStepResult(runningStep.Id)!.Status);
        Assert.Equal(StepStatus.Failed, db.GetStepResult(awaitingStep.Id)!.Status);
        Assert.Equal(StepStatus.Failed, db.GetStepResult(fixingStep.Id)!.Status);
        Assert.Equal(StepStatus.Completed, db.GetStepResult(completedStep.Id)!.Status);
        Assert.Equal(StepStatus.Pending, db.GetStepResult(pendingStep.Id)!.Status);
    }

    [Fact]
    public void RecoverStaleRunsNoStaleRuns()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project3", "git@github.com:user/project3.git", "main");

        var run = db.InsertRun(repo.Id, "feat", "abc", "def");
        db.UpdateRunStatus(run.Id, RunStatus.Completed);

        Assert.Equal(0, db.RecoverStaleRuns("daemon crashed"));
    }
}
