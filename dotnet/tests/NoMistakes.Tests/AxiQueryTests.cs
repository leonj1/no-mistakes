using System.Text.Json;
using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// The read-only axi commands - home, status, logs - as document builders over
/// a real database, asserting each command's output against the slice-8a
/// rendered shapes. Ports the home/status/logs coverage from Go's
/// internal/cli/axi_test.go (resolveRun, empty-state help, outcome mapping,
/// home other-branch isolation) plus the runAxiLogs tail/full/missing paths.
/// </summary>
public sealed class AxiQueryTests
{
    private static string FindingsJson(string summary, params object[] items) =>
        JsonSerializer.Serialize(new { findings = items, summary });

    private static Repo InsertRepo(Database db) =>
        db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

    private static Run InsertRunWithStatus(Database db, Repo repo, string branch, string status, string head = "abcdef1234567890")
    {
        var run = db.InsertRun(repo.Id, branch, head, "base");
        db.UpdateRunStatus(run.Id, status);
        return db.GetRun(run.Id)!;
    }

    // --- axi home ---

    [Fact]
    public void HomeRendersIdentityActiveRunGateAndHelp()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var run = InsertRunWithStatus(db, repo, "feature", RunStatus.Running);
        var step = db.InsertStepResult(run.Id, StepName.Review);
        db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
        db.SetStepFindings(step.Id, FindingsJson("needs a decision"));

        var output = AxiQuery.Home(db, repo, "feature", daemonRunning: false, binDisplay: "~/bin/no-mistakes");

        Assert.Equal(0, output.ExitCode);
        var doc = output.Doc;
        // Identity block renders in stable order before the run content.
        var identity =
            "bin: ~/bin/no-mistakes\n" +
            "description: \"" + AxiQuery.SkillDescription + "\"\n" +
            "repo: /home/user/project\n" +
            "current_branch: feature\n" +
            "daemon: stopped\n";
        Assert.StartsWith(identity, doc);
        Assert.Contains("active_run:\n  id: \"" + run.Id + "\"\n", doc);
        Assert.Contains("gate:\n  step: review\n  status: awaiting_approval\n", doc);
        Assert.Contains("summary: needs a decision", doc);
        Assert.Contains("Run `no-mistakes axi respond --action approve` to clear the current gate", doc);
        Assert.Contains("How to drive the pipeline", doc);
        // The recent-runs table follows with the aggregate count.
        Assert.Contains("count: 1 of 1 total\n", doc);
        Assert.Contains("runs[1]{id,branch,status,head,pr}:\n", doc);
    }

    [Fact]
    public void HomeWithActiveRunButNoGatePointsAtStatus()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        InsertRunWithStatus(db, repo, "feature", RunStatus.Running);

        var doc = AxiQuery.Home(db, repo, "feature", false, "bin").Doc;

        Assert.Contains("Run `no-mistakes axi status` to inspect the active run", doc);
        Assert.DoesNotContain("gate:", doc);
    }

    [Fact]
    public void HomeEmptyRepoShowsZeroRunsAndStartHelp()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);

        var output = AxiQuery.Home(db, repo, "", daemonRunning: true, binDisplay: "bin");

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("current_branch: unknown\n", output.Doc);
        Assert.Contains("daemon: running\n", output.Doc);
        Assert.Contains("runs: 0 runs yet in this repository\n", output.Doc);
        Assert.DoesNotContain("count:", output.Doc);
        Assert.Contains("no-mistakes axi run --intent", output.Doc);
    }

    /// <summary>
    /// Ports TestAxiHomeStartsCurrentBranchWhenOtherBranchIsActive: another
    /// branch's active run is disclosed as other_branch_active_run without its
    /// gate, and the agent is told to start its own run, not act on the other
    /// branch.
    /// </summary>
    [Fact]
    public void HomeShowsOtherBranchActiveRunWithoutItsGate()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var other = InsertRunWithStatus(db, repo, "feature/other", RunStatus.Running, head: "head-other");
        var step = db.InsertStepResult(other.Id, StepName.Review);
        db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
        db.SetStepFindings(step.Id, FindingsJson("other branch gate"));

        var doc = AxiQuery.Home(db, repo, "feature/current", false, "bin").Doc;

        Assert.Contains("current_branch: feature/current", doc);
        Assert.Contains("other_branch_active_run:", doc);
        Assert.Contains("branch: feature/other", doc);
        Assert.Contains("no-mistakes axi run --intent", doc);
        Assert.Contains("Another active run is on feature/other; leave it alone unless you are working on that branch", doc);
        Assert.DoesNotContain("\nactive_run:", doc);
        Assert.DoesNotContain("gate:", doc);
        Assert.DoesNotContain("no-mistakes axi respond --action approve", doc);
        Assert.DoesNotContain("no-mistakes axi abort", doc);
    }

    [Fact]
    public void HomeCapsRecentRunsAtLimitNewestFirst()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        for (var i = 0; i < 12; i++)
        {
            InsertRunWithStatus(db, repo, $"b{i}", RunStatus.Completed);
        }

        var doc = AxiQuery.Home(db, repo, "", false, "bin").Doc;

        Assert.Contains("count: 10 of 12 total\n", doc);
        Assert.Contains("runs[10]{id,branch,status,head,pr}:\n", doc);
        // Newest first: the last inserted branch is in the table, the first two are aged out.
        Assert.Contains(",b11,completed,", doc);
        Assert.DoesNotContain(",b0,completed,", doc);
        Assert.DoesNotContain(",b1,completed,", doc);
    }

    // --- axi status ---

    [Fact]
    public void StatusRendersRunObjectAndGate()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var run = InsertRunWithStatus(db, repo, "feature", RunStatus.Running);
        var step = db.InsertStepResult(run.Id, StepName.Review);
        db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
        db.SetStepFindings(step.Id, FindingsJson("s", new
        {
            id = "F1",
            severity = "blocking",
            file = "main.go",
            action = "ask_user",
            description = "check this",
        }));

        var output = AxiQuery.Status(db, repo, "", "feature");

        Assert.Equal(0, output.ExitCode);
        var doc = output.Doc;
        Assert.StartsWith("run:\n  id: \"" + run.Id + "\"\n  branch: feature\n  status: running\n", doc);
        Assert.Contains("gate:\n  step: review\n  status: awaiting_approval\n  summary: s\n", doc);
        Assert.Contains("findings[1]{id,severity,file,action,description}:\n", doc);
        Assert.Contains("F1,blocking,main.go,ask_user,check this", doc);
        // The gate help mentions `outcome:` inline; only a top-level outcome
        // field (start of line) would be wrong for a non-terminal run.
        Assert.DoesNotContain("\noutcome:", doc);
    }

    [Fact]
    public void StatusTerminalRunShowsOutcomeAndError()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var run = db.InsertRun(repo.Id, "feature", "abc", "base");
        db.UpdateRunErrorStatus(run.Id, "boom", RunStatus.Failed);

        var doc = AxiQuery.Status(db, repo, "", "feature").Doc;

        Assert.Contains("outcome: failed\n", doc);
        Assert.Contains("error: boom", doc);
    }

    [Fact]
    public void StatusCompletedRunReportsPassedOutcome()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        InsertRunWithStatus(db, repo, "feature", RunStatus.Completed);

        var doc = AxiQuery.Status(db, repo, "", "feature").Doc;

        Assert.Contains("outcome: passed\n", doc);
        Assert.DoesNotContain("error:", doc);
    }

    [Fact]
    public void StatusUnknownExplicitRunIdIsAnError()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);

        var output = AxiQuery.Status(db, repo, "nope", "");

        Assert.Equal(1, output.ExitCode);
        Assert.StartsWith("error:", output.Doc);
        Assert.Contains("run \\\"nope\\\" not found", output.Doc);
    }

    /// <summary>Ports TestStatusEmptyHelpIncludesRequiredIntent.</summary>
    [Fact]
    public void StatusEmptyHelpIncludesRequiredIntent()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);

        var output = AxiQuery.Status(db, repo, "", "");

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("runs: 0 runs yet in this repository\n", output.Doc);
        Assert.Contains("--intent", output.Doc);
    }

    /// <summary>Ports TestResolveRunPrefersCurrentBranchLatestRun.</summary>
    [Fact]
    public void ResolveRunPrefersCurrentBranchLatestRun()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var current = InsertRunWithStatus(db, repo, "feature/current", RunStatus.Completed, head: "head-current");
        InsertRunWithStatus(db, repo, "feature/other", RunStatus.Running, head: "head-other");

        var got = AxiQuery.ResolveRun(db, repo, "", "feature/current");

        Assert.NotNull(got);
        Assert.Equal(current.Id, got!.Id);
    }

    [Fact]
    public void ResolveRunFallsBackToRepoActiveThenMostRecent()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        Assert.Null(AxiQuery.ResolveRun(db, repo, "", ""));

        var done = InsertRunWithStatus(db, repo, "a", RunStatus.Completed);
        Assert.Equal(done.Id, AxiQuery.ResolveRun(db, repo, "", "")!.Id);

        var active = InsertRunWithStatus(db, repo, "b", RunStatus.Running);
        Assert.Equal(active.Id, AxiQuery.ResolveRun(db, repo, "", "")!.Id);

        // An explicit ID wins over everything.
        Assert.Equal(done.Id, AxiQuery.ResolveRun(db, repo, done.Id, "b")!.Id);
    }

    /// <summary>Ports TestOutcomeFor.</summary>
    [Theory]
    [InlineData(RunStatus.Completed, "passed")]
    [InlineData(RunStatus.Failed, "failed")]
    [InlineData(RunStatus.Cancelled, "cancelled")]
    [InlineData("weird", "weird")]
    public void OutcomeForMapsTerminalStatuses(string status, string want)
    {
        Assert.Equal(want, AxiQuery.OutcomeFor(status));
    }

    // --- axi logs ---

    private static (Paths, Run) LogsFixture(TempDir tmp, Database db, Repo repo, int lineCount)
    {
        var paths = Paths.WithRoot(tmp.File("nm-home"));
        paths.EnsureDirs();
        var run = db.InsertRun(repo.Id, "feature", "abc", "base");
        db.UpdateRunStatus(run.Id, RunStatus.Running);
        var dir = paths.RunLogDir(run.Id);
        Directory.CreateDirectory(dir);
        var lines = Enumerable.Range(1, lineCount).Select(i => $"line-{i}");
        File.WriteAllText(Path.Combine(dir, "test.log"), string.Join("\n", lines) + "\n");
        return (paths, db.GetRun(run.Id)!);
    }

    [Fact]
    public void LogsShortLogShowsEverythingWithTotal()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var (paths, run) = LogsFixture(tmp, db, repo, 3);

        var output = AxiQuery.Logs(paths, db, repo, "test", "", full: false, branch: "feature");

        Assert.Equal(0, output.ExitCode);
        var doc = output.Doc;
        Assert.StartsWith("step: test\nrun: \"" + run.Id + "\"\nlines: 3 total\nlog[3]{line}:\n", doc);
        Assert.Contains("  line-1\n  line-2\n  line-3", doc);
        Assert.DoesNotContain("--full", doc);
    }

    [Fact]
    public void LogsLongLogTailsWithDisclosureAndFullHelp()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var (paths, _) = LogsFixture(tmp, db, repo, 45);

        var doc = AxiQuery.Logs(paths, db, repo, "test", "", full: false, branch: "feature").Doc;

        Assert.Contains("lines: 40 of 45 total (tail)\n", doc);
        Assert.Contains("log[40]{line}:\n", doc);
        Assert.Contains("  line-6\n", doc);
        Assert.DoesNotContain("line-5\n", doc);
        Assert.Contains("  line-45", doc);
        Assert.Contains("Run `no-mistakes axi logs --step test --full` to see the entire log", doc);
    }

    [Fact]
    public void LogsFullShowsEntireLog()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var (paths, _) = LogsFixture(tmp, db, repo, 45);

        var doc = AxiQuery.Logs(paths, db, repo, "test", "", full: true, branch: "feature").Doc;

        Assert.Contains("lines: 45 total\n", doc);
        Assert.Contains("log[45]{line}:\n", doc);
        Assert.Contains("  line-1\n", doc);
    }

    [Fact]
    public void LogsMissingFileReportsNoLogRecorded()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var (paths, run) = LogsFixture(tmp, db, repo, 1);

        var output = AxiQuery.Logs(paths, db, repo, "lint", "", full: false, branch: "feature");

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("step: lint\nrun: \"" + run.Id + "\"\n", output.Doc);
        Assert.Contains("no log recorded for step \\\"lint\\\" in this run", output.Doc);
    }

    /// <summary>Ports TestLogsNoRunHelpIncludesRequiredIntent.</summary>
    [Fact]
    public void LogsWithoutAnyRunIsAnErrorWithIntentHelp()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = InsertRepo(db);
        var paths = Paths.WithRoot(tmp.File("nm-home"));

        var output = AxiQuery.Logs(paths, db, repo, "test", "", full: false, branch: "");

        Assert.Equal(1, output.ExitCode);
        Assert.Contains("error: no run found to read logs from\n", output.Doc);
        Assert.Contains("--intent", output.Doc);
    }

    [Fact]
    public void ValidateLogsStepRejectsMissingAndUnknownSteps()
    {
        var missing = AxiQuery.ValidateLogsStep("");
        Assert.NotNull(missing);
        Assert.Equal(2, missing!.ExitCode);
        Assert.Contains("error: \"--step is required\"\n", missing.Doc);
        Assert.Contains("Valid steps: intent, rebase, review, test, document, lint, push, pr, ci", missing.Doc);

        var unknown = AxiQuery.ValidateLogsStep("babysit");
        Assert.NotNull(unknown);
        Assert.Equal(2, unknown!.ExitCode);
        Assert.Contains("unknown step \\\"babysit\\\"", unknown.Doc);

        Assert.Null(AxiQuery.ValidateLogsStep("ci"));
    }

    // --- shared helpers ---

    [Fact]
    public void RepoInitHelpOnlyFiresOnUninitializedRepo()
    {
        Assert.Single(AxiQuery.RepoInitHelp("repo not initialized (run 'no-mistakes init' first)"));
        Assert.Empty(AxiQuery.RepoInitHelp("not in a git repository"));
    }

    [Fact]
    public void CollapseHomeRewritesLeadingHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("~", AxiQuery.CollapseHome(home));
        Assert.Equal(
            "~" + Path.DirectorySeparatorChar + "bin",
            AxiQuery.CollapseHome(Path.Combine(home, "bin")));
        Assert.Equal("/opt/bin", AxiQuery.CollapseHome("/opt/bin"));
    }
}
