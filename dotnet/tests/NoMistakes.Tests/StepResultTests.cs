using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>Ported from Go's internal/db/step_test.go.</summary>
public sealed class StepResultTests
{
    [Fact]
    public void GetStepResultLegacyBabysitStepName()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");
        using var db = Database.Open(path);
        var repo = db.InsertRepo("/tmp/repo", "git@github.com:test/repo.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");

        // A row stored under the retired "babysit" step name normalizes to "ci".
        using (var raw = DataTestSupport.OpenRaw(path))
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText =
                "INSERT INTO step_results (id, run_id, step_name, step_order, status) VALUES ('step1', $run, 'babysit', 7, $status)";
            cmd.Parameters.AddWithValue("$run", run.Id);
            cmd.Parameters.AddWithValue("$status", StepStatus.Pending);
            cmd.ExecuteNonQuery();
        }

        var step = db.GetStepResult("step1");
        Assert.NotNull(step);
        Assert.Equal(StepName.Ci, step!.StepName);
    }

    [Fact]
    public void StepInsertAndGet()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        var step = db.InsertStepResult(run.Id, StepName.Review);
        Assert.NotEqual(string.Empty, step.Id);
        Assert.Equal(StepName.Review, step.StepName);
        Assert.Equal(StepName.Order(StepName.Review), step.StepOrder);
        Assert.Equal(StepStatus.Pending, step.Status);

        var got = db.GetStepResult(step.Id);
        Assert.NotNull(got);
        Assert.Equal(StepName.Review, got!.StepName);
    }

    [Fact]
    public void StepsByRun()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        // Insert out of order to verify execution ordering.
        db.InsertStepResult(run.Id, StepName.Lint);
        db.InsertStepResult(run.Id, StepName.Review);
        db.InsertStepResult(run.Id, StepName.Test);

        var steps = db.GetStepsByRun(run.Id);
        Assert.Equal(3, steps.Count);
        Assert.Equal(StepName.Review, steps[0].StepName);
        Assert.Equal(StepName.Test, steps[1].StepName);
        Assert.Equal(StepName.Lint, steps[2].StepName);
    }

    [Fact]
    public void StartStep()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.StartStep(step.Id);
        var got = db.GetStepResult(step.Id);
        Assert.Equal(StepStatus.Running, got!.Status);
        Assert.NotNull(got.StartedAt);
    }

    [Fact]
    public void CompleteStep()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.CompleteStep(step.Id, 0, 1500, "/logs/run-1/review.log");
        var got = db.GetStepResult(step.Id);
        Assert.Equal(StepStatus.Completed, got!.Status);
        Assert.Equal(0, got.ExitCode);
        Assert.Equal(1500, got.DurationMs);
        Assert.Equal("/logs/run-1/review.log", got.LogPath);
        Assert.NotNull(got.CompletedAt);
    }

    [Fact]
    public void CompleteStepWithStatus()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.CompleteStepWithStatus(step.Id, StepStatus.Skipped, 0, 1500, "/logs/run-1/review.log");
        var got = db.GetStepResult(step.Id);
        Assert.Equal(StepStatus.Skipped, got!.Status);
        Assert.Equal(0, got.ExitCode);
        Assert.Equal(1500, got.DurationMs);
        Assert.Equal("/logs/run-1/review.log", got.LogPath);
        Assert.NotNull(got.CompletedAt);
    }

    [Fact]
    public void UpdateStepStatusWithDuration()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Test);

        db.UpdateStepStatusWithDuration(step.Id, StepStatus.AwaitingApproval, 1200);
        var got = db.GetStepResult(step.Id);
        Assert.Equal(StepStatus.AwaitingApproval, got!.Status);
        Assert.Equal(1200, got.DurationMs);
    }

    [Fact]
    public void FailStep()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.FailStep(step.Id, "agent crashed", 1500);
        var got = db.GetStepResult(step.Id);
        Assert.Equal(StepStatus.Failed, got!.Status);
        Assert.Equal("agent crashed", got.Error);
        Assert.Equal(1500, got.DurationMs);
    }

    [Fact]
    public void SetStepFindings()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        const string findings = "[{\"severity\":\"warning\",\"message\":\"unused variable\"}]";
        db.SetStepFindings(step.Id, findings);
        Assert.Equal(findings, db.GetStepResult(step.Id)!.FindingsJson);
    }

    [Fact]
    public void ClearStepFindings()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.SetStepFindings(step.Id, "[{\"severity\":\"warning\",\"message\":\"unused variable\"}]");
        db.ClearStepFindings(step.Id);
        Assert.Null(db.GetStepResult(step.Id)!.FindingsJson);
    }

    [Fact]
    public void UpdateStepStatus()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        var step = db.InsertStepResult(run.Id, StepName.Review);

        db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
        Assert.Equal(StepStatus.AwaitingApproval, db.GetStepResult(step.Id)!.Status);
    }
}
