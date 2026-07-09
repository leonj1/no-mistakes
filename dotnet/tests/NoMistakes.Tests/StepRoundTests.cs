using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>Ported from Go's internal/db/round_test.go.</summary>
public sealed class StepRoundTests
{
    [Fact]
    public void StepRoundInsertAndGet()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        const string findings = "{\"findings\":[{\"id\":\"review-1\",\"severity\":\"warning\",\"description\":\"unused var\"}],\"summary\":\"1 issue\"}";
        var r = db.InsertStepRound(step.Id, 1, "initial", findings, null, 1200);
        Assert.NotEqual(string.Empty, r.Id);
        Assert.Equal(step.Id, r.StepResultId);
        Assert.Equal(1, r.Round);
        Assert.Equal("initial", r.Trigger);
        Assert.Equal(findings, r.FindingsJson);
        Assert.Equal(1200, r.DurationMs);
        Assert.NotEqual(0, r.CreatedAt);
        Assert.Null(r.SelectedFindingIds);
        Assert.Null(r.SelectionSource);
        Assert.Null(r.FixSummary);
    }

    [Fact]
    public void StepRoundNullFindings()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Test);

        var r = db.InsertStepRound(step.Id, 1, "initial", null, null, 500);
        Assert.Null(r.FindingsJson);
    }

    [Fact]
    public void GetRoundsByStep()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Lint);

        const string findings1 = "{\"findings\":[{\"id\":\"lint-1\",\"severity\":\"error\",\"description\":\"missing check\"}],\"summary\":\"1 error\"}";
        db.InsertStepRound(step.Id, 1, "initial", findings1, null, 800);
        const string fixSummary = "fix missing check";
        db.InsertStepRound(step.Id, 2, "auto_fix", null, fixSummary, 600);

        var rounds = db.GetRoundsByStep(step.Id);
        Assert.Equal(2, rounds.Count);
        Assert.Equal(1, rounds[0].Round);
        Assert.Equal("initial", rounds[0].Trigger);
        Assert.NotNull(rounds[0].FindingsJson);
        Assert.Null(rounds[0].FixSummary);
        Assert.Equal(2, rounds[1].Round);
        Assert.Equal("auto_fix", rounds[1].Trigger);
        Assert.Null(rounds[1].FindingsJson);
        Assert.Equal(fixSummary, rounds[1].FixSummary);
    }

    [Fact]
    public void GetRoundsByStepEmpty()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Push);

        Assert.Empty(db.GetRoundsByStep(step.Id));
    }

    [Fact]
    public void StepFixSummaries()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        const string findings = "{\"findings\":[{\"id\":\"review-1\",\"severity\":\"warning\",\"description\":\"x\"}],\"summary\":\"1\"}";
        db.InsertStepRound(step.Id, 1, "initial", findings, null, 100);
        const string s1 = "handle nil pointer in executor";
        db.InsertStepRound(step.Id, 2, "auto_fix", null, s1, 100);
        // Legacy fix round without a recorded summary still counts as a fix.
        db.InsertStepRound(step.Id, 3, "user_fix", null, null, 100);
        const string s2 = "tighten log path validation";
        db.InsertStepRound(step.Id, 4, "auto_fix", null, s2, 100);

        var got = db.StepFixSummaries(step.Id);
        Assert.Equal(new[] { s1, "", s2 }, got);
    }

    [Fact]
    public void StepFixSummariesNoFixRounds()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Lint);
        db.InsertStepRound(step.Id, 1, "initial", null, null, 100);

        Assert.Empty(db.StepFixSummaries(step.Id));
    }

    [Fact]
    public void StepRoundCascadeDelete()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        db.InsertStepRound(step.Id, 1, "initial", null, null, 100);

        db.DeleteRepo(repo.Id);
        Assert.Empty(db.GetRoundsByStep(step.Id));
    }

    [Fact]
    public void SetStepRoundSelectedFindingIds()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        const string findings = "{\"findings\":[{\"id\":\"review-1\",\"severity\":\"warning\",\"description\":\"x\"},{\"id\":\"review-2\",\"severity\":\"error\",\"description\":\"y\"}],\"summary\":\"2\"}";
        var r = db.InsertStepRound(step.Id, 1, "initial", findings, null, 50);

        const string selected = "[\"review-1\"]";
        db.SetStepRoundSelection(r.Id, selected, Database.RoundSelectionSourceUser);

        var rounds = db.GetRoundsByStep(step.Id);
        Assert.Single(rounds);
        Assert.Equal(selected, rounds[0].SelectedFindingIds);
        Assert.Equal(Database.RoundSelectionSourceUser, rounds[0].SelectionSource);

        // Clearing resets both columns to NULL.
        db.SetStepRoundSelection(r.Id, null, Database.RoundSelectionSourceUser);
        rounds = db.GetRoundsByStep(step.Id);
        Assert.Null(rounds[0].SelectedFindingIds);
        Assert.Null(rounds[0].SelectionSource);
    }
}
