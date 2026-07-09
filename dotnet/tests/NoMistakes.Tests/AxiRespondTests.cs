using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using NoMistakes.Git;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// The `axi respond` verb dispatch (slice 8c.2a): approve/fix/skip resolving
/// a parked gate through the daemon's respond handler, plus the action
/// validation and lookup errors. The runner parks a review gate and consumes
/// the response via RunManager.RegisterResponder, standing in for the slice-9
/// executor. In the "daemon" collection because the CLI tests mutate NM_HOME,
/// the process working directory, and the daemon socket.
/// </summary>
[Collection("daemon")]
public sealed class AxiRespondTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string FindingsJson =
        """{"findings":[{"id":"f1","severity":"major","file":"a.txt","description":"needs a look","action":"ask-user"}],"summary":"1 blocking issue"}""";

    private static int RunCli(string[] args, out string stdoutText, out string stderrText)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = new CliApp(stdout, stderr).Run(args);
        stdoutText = stdout.ToString();
        stderrText = stderr.ToString();
        return code;
    }

    private sealed record RespondSeen(
        string Step,
        string Action,
        List<string>? FindingIds,
        Dictionary<string, string>? Instructions,
        List<Finding>? Added);

    /// <summary>
    /// A pipeline runner that parks its run at a review gate and resumes when
    /// the responder receives an action: skip marks the step skipped, any
    /// other action completes it, and the run completes. The stand-in for the
    /// slice-9 executor's waitForApproval loop.
    /// </summary>
    private static (RunManager Manager, Task<RespondSeen> Seen) ParkedGateManager(Database db)
    {
        var seen = new TaskCompletionSource<RespondSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunManager manager = null!;
        manager = new RunManager(db, async (run, _, tok) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Running);
            var step = db.InsertStepResult(run.Id, StepName.Review);
            db.SetStepFindings(step.Id, FindingsJson);
            manager.RegisterResponder(run.Id,
                (s, a, ids, instr, added) => seen.TrySetResult(new RespondSeen(s, a, ids, instr, added)));
            // Parked marker before the status flip, per the executor invariant.
            db.SetRunAwaitingAgent(run.Id);
            db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
            var response = await seen.Task.WaitAsync(tok);
            db.ClearRunAwaitingAgent(run.Id);
            db.UpdateStepStatus(step.Id,
                response.Action == ApprovalAction.Skip ? StepStatus.Skipped : StepStatus.Completed);
            db.UpdateRunStatus(run.Id, RunStatus.Completed);
        });
        return (manager, seen.Task);
    }

    /// <summary>A working repo on a feature branch, registered in the DB.</summary>
    private static async Task<(string Root, Repo Repo)> SetupRepoAsync(TempDir repoTmp, Database db)
    {
        GitTestSupport.InitRepo(repoTmp.Path);
        File.WriteAllText(Path.Combine(repoTmp.Path, "a.txt"), "one\n");
        GitTestSupport.Git(repoTmp.Path, "add", "a.txt");
        GitTestSupport.Git(repoTmp.Path, "commit", "-q", "-m", "initial");
        GitTestSupport.Git(repoTmp.Path, "checkout", "-q", "-b", "feature");
        // Register under the symlink-resolved root FindGitRoot reports
        // (macOS /var vs /private/var).
        var root = await new GitClient().FindGitRootAsync(repoTmp.Path);
        var repo = db.InsertRepoWithId("repo-1", root, "git@github.com:u/r.git", "main");
        return (root, repo);
    }

    private static async Task WaitForParkedAsync(Database db, string runId)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (db.GetRun(runId)!.AwaitingAgentSince == null)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "run never parked at the gate");
            await Task.Delay(25);
        }
    }

    [Theory]
    [InlineData("approve", "completed")]
    [InlineData("skip", "skipped")]
    public async Task RespondVerbResolvesParkedGateAndDrivesToOutcome(string action, string wantStepStatus)
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(["axi", "respond", "--action", action], out var got, out _);
            Assert.Equal(0, code);

            // The action reached the parked step.
            var response = await seen.WaitAsync(Timeout);
            Assert.Equal(StepName.Review, response.Step);
            Assert.Equal(action, response.Action);
            Assert.Null(response.FindingIds);

            // The gate resolved and the drive loop reported the outcome.
            Assert.Contains("outcome: passed", got);
            Assert.Contains(wantStepStatus, got);
            Assert.DoesNotContain("awaiting_agent: parked", got);
            Assert.Equal(RunStatus.Completed, db.GetRun(runId)!.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public async Task RespondFixWithoutFindingSelectionIsUsageErrorAndLeavesGateParked()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(["axi", "respond", "--action", "fix"], out var got, out _);
            Assert.Equal(2, code);
            Assert.Contains("--action fix requires --findings", got);
            Assert.Contains("list finding IDs", got);

            // Nothing reached the responder; the gate is still parked.
            Assert.False(seen.IsCompleted);
            Assert.NotNull(db.GetRun(runId)!.AwaitingAgentSince);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    /// <summary>
    /// The --findings payload path: the comma-separated IDs are split and
    /// trimmed, and the --instructions note is fanned out to every selected
    /// finding (Go builds the per-finding instructions map from one note).
    /// </summary>
    [Fact]
    public async Task RespondFixWithFindingsAndInstructionsRoutesSelectionToParkedGate()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(
                ["axi", "respond", "--action", "fix", "--findings", " f1, ,f2 ", "--instructions", "prefer a table test"],
                out var got, out _);
            Assert.Equal(0, code);

            var response = await seen.WaitAsync(Timeout);
            Assert.Equal(StepName.Review, response.Step);
            Assert.Equal(ApprovalAction.Fix, response.Action);
            Assert.Equal(new List<string> { "f1", "f2" }, response.FindingIds);
            Assert.Equal(new Dictionary<string, string>
            {
                ["f1"] = "prefer a table test",
                ["f2"] = "prefer a table test",
            }, response.Instructions);
            Assert.Null(response.Added);

            Assert.Contains("outcome: passed", got);
            Assert.Equal(RunStatus.Completed, db.GetRun(runId)!.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    /// <summary>
    /// The --add-finding payload path: the JSON finding is decoded and routed
    /// to the parked step, with no ID selection required. An --instructions
    /// note without --findings is dropped (Go applies it only to selected
    /// finding IDs).
    /// </summary>
    [Fact]
    public async Task RespondFixWithAddFindingRoutesAddedFindingToParkedGate()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(
                [
                    "axi", "respond", "--action", "fix",
                    "--add-finding", """{"description":"missing error path test","action":"auto-fix","severity":"minor","file":"b.txt","line":3}""",
                    "--instructions", "orphan note",
                ],
                out var got, out _);
            Assert.Equal(0, code);

            var response = await seen.WaitAsync(Timeout);
            Assert.Equal(StepName.Review, response.Step);
            Assert.Equal(ApprovalAction.Fix, response.Action);
            Assert.Null(response.FindingIds);
            Assert.Null(response.Instructions);
            var added = Assert.Single(response.Added!);
            Assert.Equal("missing error path test", added.Description);
            Assert.Equal("auto-fix", added.Action);
            Assert.Equal("minor", added.Severity);
            Assert.Equal("b.txt", added.File);
            Assert.Equal(3, added.Line);

            Assert.Contains("outcome: passed", got);
            Assert.Equal(RunStatus.Completed, db.GetRun(runId)!.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    /// <summary>
    /// Malformed --add-finding payloads are exit-2 usage errors that leave the
    /// gate parked: broken JSON and a JSON object missing the required
    /// description.
    /// </summary>
    [Fact]
    public async Task RespondFixWithMalformedAddFindingIsUsageErrorAndLeavesGateParked()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(
                ["axi", "respond", "--action", "fix", "--add-finding", "{not json"],
                out var got, out _);
            Assert.Equal(2, code);
            Assert.Contains("invalid --add-finding", got);
            Assert.Contains("Expected a JSON object", got);

            code = RunCli(
                ["axi", "respond", "--action", "fix", "--add-finding", """{"action":"auto-fix"}"""],
                out got, out _);
            Assert.Equal(2, code);
            Assert.Contains("invalid --add-finding: description is required", got);

            // Nothing reached the responder; the gate is still parked.
            Assert.False(seen.IsCompleted);
            Assert.NotNull(db.GetRun(runId)!.AwaitingAgentSince);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public void ParseAddFindingDecodesAFullFindingObject()
    {
        var f = AxiDrive.ParseAddFinding(
            """{"id":"x1","description":"desc","action":"ask-user","severity":"major","file":"a.txt","line":7,"source":"agent"}""");
        Assert.Equal("x1", f.Id);
        Assert.Equal("desc", f.Description);
        Assert.Equal("ask-user", f.Action);
        Assert.Equal("major", f.Severity);
        Assert.Equal("a.txt", f.File);
        Assert.Equal(7, f.Line);
        Assert.Equal("agent", f.Source);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void ParseAddFindingRejectsMalformedJson(string raw)
    {
        Assert.Throws<FormatException>(() => AxiDrive.ParseAddFinding(raw));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"description":"   "}""")]
    [InlineData("""{"action":"auto-fix"}""")]
    public void ParseAddFindingRequiresADescription(string raw)
    {
        var ex = Assert.Throws<FormatException>(() => AxiDrive.ParseAddFinding(raw));
        Assert.Equal("description is required", ex.Message);
    }

    [Fact]
    public void SplitCsvTrimsAndDropsEmptyParts()
    {
        Assert.Empty(AxiDrive.SplitCsv(""));
        Assert.Empty(AxiDrive.SplitCsv("  "));
        Assert.Empty(AxiDrive.SplitCsv(" , ,"));
        Assert.Equal(new List<string> { "a" }, AxiDrive.SplitCsv("a"));
        Assert.Equal(new List<string> { "a", "b" }, AxiDrive.SplitCsv(" a , ,b "));
    }

    /// <summary>
    /// The fix verb's daemon-side gate resolution: a respond call carrying
    /// selected finding IDs routes them to the parked step, independent of
    /// any repo/branch resolution.
    /// </summary>
    [Fact]
    public async Task FixVerbOverIpcRoutesSelectedFindingsToParkedGate()
    {
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var repo = db.InsertRepoWithId("repo-1", "/home/user/project", "git@github.com:u/r.git", "main");

        var (manager, seen) = ParkedGateManager(db);
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
        await WaitForParkedAsync(db, runId);

        using var client = await IpcClient.DialAsync(paths.Socket);
        await AxiDrive.SendRespondAsync(
            client, runId, StepName.Review, ApprovalAction.Fix,
            new List<string> { "f1" }, null, null, CancellationToken.None);

        var response = await seen.WaitAsync(Timeout);
        Assert.Equal(StepName.Review, response.Step);
        Assert.Equal(ApprovalAction.Fix, response.Action);
        Assert.Equal(new List<string> { "f1" }, response.FindingIds);
    }

    [Fact]
    public async Task RespondForRunWithoutRegisteredResponderErrors()
    {
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);

        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);
        await using var host = await StartDaemonAsync(paths, db, manager);

        using var client = await IpcClient.DialAsync(paths.Socket);
        var ex = await Assert.ThrowsAsync<IpcRpcException>(() => AxiDrive.SendRespondAsync(
            client, "run-404", StepName.Review, ApprovalAction.Approve,
            null, null, null, CancellationToken.None));
        Assert.Contains("no active executor for run run-404", ex.Message);
    }

    [Fact]
    public async Task RespondErrorsWhenNoActiveRunOrNoParkedStep()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupRepoAsync(repoTmp, db);

        // Runner that runs without ever parking a gate.
        var manager = new RunManager(db, async (run, _, tok) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Running);
            await Task.Delay(System.Threading.Timeout.Infinite, tok);
        });
        await using var host = await StartDaemonAsync(paths, db, manager);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            // No run at all on the branch.
            var code = RunCli(["axi", "respond", "--action", "approve"], out var got, out _);
            Assert.Equal(1, code);
            Assert.Contains("no active run to respond to", got);
            Assert.Contains("axi run", got);

            // An active run, but nothing awaiting approval.
            var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (db.GetRun(runId)!.Status != RunStatus.Running)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "run never started running");
                await Task.Delay(25);
            }
            code = RunCli(["axi", "respond", "--action", "approve"], out got, out _);
            Assert.Equal(1, code);
            Assert.Contains("no step is awaiting approval", got);
            Assert.Contains("axi status", got);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    /// <summary>
    /// Action validation runs before any environment is opened (Go order):
    /// these fail with exit 2 even with no NM_HOME, repo, or daemon.
    /// </summary>
    [Fact]
    public void RespondValidatesActionBeforeTouchingAnyEnvironment()
    {
        var code = RunCli(["axi", "respond"], out var got, out _);
        Assert.Equal(2, code);
        Assert.Contains("--action is required", got);
        Assert.Contains("approve|fix|skip", got);

        code = RunCli(["axi", "respond", "--action", "bogus"], out got, out _);
        Assert.Equal(2, code);
        // The message carries quotes, so TOON escapes them inside the quoted
        // error value.
        Assert.Contains("unknown action \\\"bogus\\\"", got);
        Assert.Contains("Valid actions: approve, fix, skip", got);
    }

    [Fact]
    public void ValidateRespondActionAcceptsTheThreeVerbs()
    {
        Assert.Null(AxiDrive.ValidateRespondAction("approve"));
        Assert.Null(AxiDrive.ValidateRespondAction("fix"));
        Assert.Null(AxiDrive.ValidateRespondAction("skip"));
        Assert.Null(AxiDrive.ValidateRespondAction("  approve  "));
        Assert.NotNull(AxiDrive.ValidateRespondAction(""));
        Assert.NotNull(AxiDrive.ValidateRespondAction("approve it"));
    }

    [Fact]
    public void GateStatusForDefaultsToAwaitingApprovalWhenStepMissing()
    {
        var rv = new RunView
        {
            Steps = { new StepView { Name = StepName.Review, Status = StepStatus.FixReview } },
        };
        Assert.Equal(StepStatus.FixReview, AxiDrive.GateStatusFor(rv, StepName.Review));
        Assert.Equal(StepStatus.AwaitingApproval, AxiDrive.GateStatusFor(rv, StepName.Test));
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
