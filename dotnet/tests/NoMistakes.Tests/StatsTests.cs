using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>Ported from Go's internal/db/stats_test.go (usage aggregation).</summary>
public sealed class StatsTests
{
    [Fact]
    public void GetStatsAggregatesReportedFixesAndRescueRuns()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repoA = db.InsertRepo("/repo/a", "git@example.com:a.git", "main");
        var repoB = db.InsertRepo("/repo/b", "git@example.com:b.git", "main");

        var runA = db.InsertRun(repoA.Id, "feature-a", "head-a", "base-a");
        var reviewA = db.InsertStepResult(runA.Id, StepName.Review);
        const string reviewAInitial = "{\"findings\":[{\"id\":\"r1\",\"severity\":\"warning\",\"description\":\"one\",\"action\":\"auto-fix\"},{\"id\":\"r2\",\"severity\":\"warning\",\"description\":\"two\",\"action\":\"auto-fix\"},{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\",\"action\":\"auto-fix\"}],\"summary\":\"three\",\"risk_level\":\"medium\",\"risk_rationale\":\"test\"}";
        const string reviewAFinal = "{\"findings\":[{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\",\"action\":\"ask-user\"}],\"summary\":\"one left\",\"risk_level\":\"medium\",\"risk_rationale\":\"test\"}";
        db.InsertStepRound(reviewA.Id, 1, "initial", reviewAInitial, null, 100);
        db.InsertStepRound(reviewA.Id, 2, "auto_fix", reviewAFinal, null, 100);

        var lintA = db.InsertStepResult(runA.Id, StepName.Lint);
        const string lintAInitial = "{\"findings\":[{\"id\":\"l1\",\"severity\":\"error\",\"description\":\"lint\",\"action\":\"auto-fix\"}],\"summary\":\"one\",\"risk_level\":\"low\",\"risk_rationale\":\"test\"}";
        db.InsertStepRound(lintA.Id, 1, "initial", lintAInitial, null, 100);
        db.InsertStepRound(lintA.Id, 2, "auto_fix", null, null, 100);

        var runB = db.InsertRun(repoB.Id, "feature-b", "head-b", "base-b");
        var testB = db.InsertStepResult(runB.Id, StepName.Test);
        const string testBInitial = "{\"findings\":[{\"id\":\"t1\",\"severity\":\"error\",\"description\":\"test\",\"action\":\"ask-user\"}],\"summary\":\"one\",\"risk_level\":\"low\",\"risk_rationale\":\"test\"}";
        db.InsertStepRound(testB.Id, 1, "initial", testBInitial, null, 100);

        var stats = db.GetStats();

        Assert.Equal(2, stats.TotalRuns);
        Assert.Equal(1, stats.RescueRuns);
        Assert.Equal(5, stats.ReportedFindings);
        Assert.Equal(3, stats.FixedFindings);

        AssertStepStat(stats.StepStats, StepName.Review, 3, 2);
        AssertStepStat(stats.StepStats, StepName.Lint, 1, 1);
        AssertStepStat(stats.StepStats, StepName.Test, 1, 0);

        Assert.Equal(2, stats.RepoStats.Count);
        Assert.Equal("/repo/a", stats.RepoStats[0].WorkingPath);
        Assert.Equal(1, stats.RepoStats[0].RescueRuns);
        Assert.Equal(3, stats.RepoStats[0].FixedFindings);
    }

    [Fact]
    public void GetStatsFallsBackToStepFindingsWhenRoundsAreMissing()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/repo/legacy", "git@example.com:legacy.git", "main");
        var run = db.InsertRun(repo.Id, "legacy", "head", "base");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        const string findings = "{\"findings\":[{\"id\":\"legacy-1\",\"severity\":\"warning\",\"description\":\"legacy\",\"action\":\"ask-user\"}],\"summary\":\"one\",\"risk_level\":\"low\",\"risk_rationale\":\"test\"}";
        db.SetStepFindings(step.Id, findings);

        var stats = db.GetStats();
        Assert.Equal(1, stats.ReportedFindings);
        Assert.Equal(0, stats.FixedFindings);
        Assert.Equal(0, stats.RescueRuns);
    }

    [Fact]
    public void FixedFindingsByStepCountsResolvedRoundFindings()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/repo/fixes", "git@example.com:fixes.git", "main");
        var run = db.InsertRun(repo.Id, "fixes", "head", "base");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        const string initial = "{\"findings\":[{\"id\":\"r1\",\"severity\":\"warning\",\"description\":\"one\"},{\"id\":\"r2\",\"severity\":\"warning\",\"description\":\"two\"},{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\"}],\"summary\":\"three\"}";
        const string final = "{\"findings\":[{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\"}],\"summary\":\"one left\"}";
        db.InsertStepRound(step.Id, 1, "initial", initial, null, 100);
        db.InsertStepRound(step.Id, 2, "auto_fix", final, null, 100);

        Assert.Equal(2, db.FixedFindingsByStep(step));
    }

    [Fact]
    public void StepFindingStatsDoesNotCountSelectedFindingsAsFixed()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/repo/fixing", "git@example.com:fixing.git", "main");
        var run = db.InsertRun(repo.Id, "fixing", "head", "base");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        const string initial = "{\"findings\":[{\"id\":\"r1\",\"severity\":\"warning\",\"description\":\"one\"},{\"id\":\"r2\",\"severity\":\"warning\",\"description\":\"two\"},{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\"}],\"summary\":\"three\"}";
        var round = db.InsertStepRound(step.Id, 1, "initial", initial, null, 100);
        db.SetStepRoundSelection(round.Id, "[\"r1\",\"r2\"]", Database.RoundSelectionSourceUser);

        var stats = db.StepFindingStats(step);
        Assert.Equal(3, stats.ReportedFindings);
        Assert.Equal(0, stats.FixedFindings);
    }

    [Fact]
    public void StepFindingStatsAddsNewFindingsToTotal()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/repo/new-findings", "git@example.com:new.git", "main");
        var run = db.InsertRun(repo.Id, "new", "head", "base");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        const string initial = "{\"findings\":[{\"id\":\"r1\",\"severity\":\"warning\",\"description\":\"one\"},{\"id\":\"r2\",\"severity\":\"warning\",\"description\":\"two\"},{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\"}],\"summary\":\"three\"}";
        const string final = "{\"findings\":[{\"id\":\"r3\",\"severity\":\"warning\",\"description\":\"three\"},{\"id\":\"r4\",\"severity\":\"warning\",\"description\":\"four\"}],\"summary\":\"two left\"}";
        db.InsertStepRound(step.Id, 1, "initial", initial, null, 100);
        db.InsertStepRound(step.Id, 2, "auto_fix", final, null, 100);

        var stats = db.StepFindingStats(step);
        Assert.Equal(4, stats.ReportedFindings);
        Assert.Equal(2, stats.FixedFindings);
    }

    private static void AssertStepStat(List<StepStats> stats, string step, int reported, int fixes)
    {
        var got = stats.FirstOrDefault(s => s.StepName == step);
        Assert.True(got != null, $"missing stats for step {step}");
        Assert.Equal(reported, got!.ReportedFindings);
        Assert.Equal(fixes, got.FixedFindings);
    }
}
