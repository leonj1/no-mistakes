using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Database-row to IPC wire-shape mapping. Ports Go's
/// internal/daemon/runinfo_test.go plus the awaiting-agent field derivation
/// from runToInfo.
/// </summary>
public class RunInfoMapperTests
{
    [Fact]
    public void RunToInfoMapsAllFields()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        db.UpdateRunPrUrl(run.Id, "https://github.com/user/project/pull/7");
        run = db.GetRun(run.Id)!;

        var info = RunInfoMapper.RunToInfo(db, run, new List<StepResult>());

        Assert.Equal(run.Id, info.Id);
        Assert.Equal(repo.Id, info.RepoId);
        Assert.Equal("feature", info.Branch);
        Assert.Equal("abc", info.HeadSha);
        Assert.Equal("def", info.BaseSha);
        Assert.Equal(RunStatus.Pending, info.Status);
        Assert.Equal("https://github.com/user/project/pull/7", info.PrUrl);
        Assert.Null(info.Error);
        Assert.False(info.AwaitingAgent);
        Assert.Null(info.AwaitingAgentSince);
        Assert.Null(info.Steps);
        Assert.Equal(run.CreatedAt, info.CreatedAt);
        Assert.Equal(run.UpdatedAt, info.UpdatedAt);
    }

    [Fact]
    public void RunToInfoDerivesAwaitingAgentFromMarker()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        db.SetRunAwaitingAgent(run.Id);
        var parked = db.GetRun(run.Id)!;
        var info = RunInfoMapper.RunToInfo(db, parked, new List<StepResult>());
        Assert.True(info.AwaitingAgent);
        Assert.NotNull(info.AwaitingAgentSince);
        Assert.Equal(parked.AwaitingAgentSince, info.AwaitingAgentSince);

        db.ClearRunAwaitingAgent(run.Id);
        var resumed = db.GetRun(run.Id)!;
        info = RunInfoMapper.RunToInfo(db, resumed, new List<StepResult>());
        Assert.False(info.AwaitingAgent);
        Assert.Null(info.AwaitingAgentSince);
    }

    [Fact]
    public void RecoveredRunIsNeverReportedParked()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        db.UpdateRunStatus(run.Id, RunStatus.Running);
        var step = db.InsertStepResult(run.Id, StepName.Review);
        db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
        db.SetRunAwaitingAgent(run.Id);

        db.RecoverStaleRuns("daemon crashed during execution");

        // A crash-recovered (now failed) run must not surface as parked
        // awaiting the agent on the wire.
        var recovered = db.GetRun(run.Id)!;
        var info = RunInfoMapper.RunToInfo(db, recovered, db.GetStepsByRun(run.Id));
        Assert.Equal(RunStatus.Failed, info.Status);
        Assert.False(info.AwaitingAgent);
        Assert.Null(info.AwaitingAgentSince);
        Assert.Equal(StepStatus.Failed, info.Steps![0].Status);
    }

    [Fact]
    public void RunToInfoIncludesSteps()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var lint = db.InsertStepResult(run.Id, StepName.Lint);
        var review = db.InsertStepResult(run.Id, StepName.Review);

        var steps = db.GetStepsByRun(run.Id);
        var info = RunInfoMapper.RunToInfo(db, db.GetRun(run.Id)!, steps);

        Assert.NotNull(info.Steps);
        Assert.Equal(2, info.Steps!.Count);
        // GetStepsByRun orders by step_order: review before lint.
        Assert.Equal(review.Id, info.Steps[0].Id);
        Assert.Equal(StepName.Review, info.Steps[0].StepName);
        Assert.Equal(lint.Id, info.Steps[1].Id);
        Assert.Equal(run.Id, info.Steps[0].RunId);
    }

    [Fact]
    public void StepToInfoIncludesFixSummaries()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        var findings = """{"findings":[{"id":"review-1","severity":"warning","description":"x"}],"summary":"1"}""";
        db.InsertStepRound(step.Id, 1, "initial", findings, null, 100);
        var sum = "handle nil pointer in executor";
        db.InsertStepRound(step.Id, 2, "auto_fix", null, sum, 100);

        var info = RunInfoMapper.StepToInfo(db, step);
        Assert.NotNull(info.FixSummaries);
        Assert.Single(info.FixSummaries!);
        Assert.Equal(sum, info.FixSummaries![0]);
        // One finding reported in round 1, none left after the fix round.
        Assert.Equal(1, info.ReportedFindings);
        Assert.Equal(1, info.FixedFindings);
    }

    [Fact]
    public void StepToInfoNoFixSummariesWithoutFixRounds()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Lint);
        db.InsertStepRound(step.Id, 1, "initial", null, null, 100);

        var info = RunInfoMapper.StepToInfo(db, step);
        Assert.Null(info.FixSummaries);
    }
}
