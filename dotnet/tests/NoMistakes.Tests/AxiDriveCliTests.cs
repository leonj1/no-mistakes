using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using NoMistakes.Git;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// The `axi run` and `axi abort` commands driven end to end through CliApp
/// against a live in-process daemon (slice 8c.1): run submission through a
/// real gate push firing the real post-receive hook, the gate decision point,
/// the worktree/branch-scoped abort, and the pre-flight guards. In the
/// "daemon" collection because it mutates NM_HOME, the process working
/// directory, and the daemon socket.
/// </summary>
[Collection("daemon")]
public sealed class AxiDriveCliTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static int RunCli(string[] args, out string stdoutText, out string stderrText)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = new CliApp(stdout, stderr).Run(args);
        stdoutText = stdout.ToString();
        stderrText = stderr.ToString();
        return code;
    }

    /// <summary>
    /// A working repo on a feature branch wired to a real bare gate under
    /// NM_HOME: post-receive hook installed (via the CLI-binary shim),
    /// push options advertised, and the "no-mistakes" remote added - the state
    /// `no-mistakes init` leaves behind.
    /// </summary>
    private static async Task<(string Root, Repo Repo)> SetupGatedRepoAsync(
        TempDir repoTmp, string homeRoot, Database db)
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

        var bareDir = Paths.WithRoot(homeRoot).RepoDir(repo.Id);
        Directory.CreateDirectory(bareDir);
        await new GitClient().RunAsync(bareDir, "init", "--bare", ".");
        GitTestSupport.Git(bareDir, "config", "receive.advertisePushOptions", "true");

        // Shim standing in for the installed no-mistakes binary so the hook's
        // notify-push runs this build against this test's NM_HOME (the
        // slice-7e.2 CLI-as-subprocess pattern).
        var cliDll = Path.Combine(AppContext.BaseDirectory, "no-mistakes.dll");
        Assert.True(File.Exists(cliDll), $"CLI assembly not found at {cliDll}");
        var shim = Path.Combine(homeRoot, "no-mistakes");
        File.WriteAllText(shim,
            "#!/bin/sh\n" +
            $"NM_HOME={PostReceiveHook.ShellSingleQuote(homeRoot)}\n" +
            "export NM_HOME\n" +
            $"exec dotnet {PostReceiveHook.ShellSingleQuote(cliDll)} \"$@\"\n");
        var hookPath = Path.Combine(bareDir, "hooks", "post-receive");
        Directory.CreateDirectory(Path.Combine(bareDir, "hooks"));
        File.WriteAllText(hookPath, PostReceiveHook.ScriptFor(shim));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shim, (UnixFileMode)0b111_101_101); // 0755
            File.SetUnixFileMode(hookPath, (UnixFileMode)0b111_101_101);
        }

        GitTestSupport.Git(repoTmp.Path, "remote", "add", Gate.RemoteName, bareDir);
        return (root, repo);
    }

    [Fact]
    public async Task AxiRunSubmitsRunThroughGateParksAtGateAndScopedAbortCancelsIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the post-receive hook is a POSIX shell script
        }

        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var (root, repo) = await SetupGatedRepoAsync(repoTmp, homeTmp.Path, db);
        var headSha = GitTestSupport.Git(repoTmp.Path, "rev-parse", "HEAD");

        // The runner immediately parks the run at a review gate and holds it
        // there until cancelled, standing in for the slice-9 executor.
        var manager = new RunManager(db, async (run, _, tok) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Running);
            var step = db.InsertStepResult(run.Id, StepName.Review);
            db.SetStepFindings(step.Id, """{"findings":[{"id":"f1","severity":"major","file":"a.txt","description":"needs a look","action":"ask-user"}],"summary":"1 blocking issue"}""");
            // Parked marker before the status flip, per the executor invariant
            // (already set once pollers observe the gate).
            db.SetRunAwaitingAgent(run.Id);
            db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
            await Task.Delay(System.Threading.Timeout.Infinite, tok);
        });
        await using var host = await StartDaemonAsync(paths, db, manager);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            // Submission: the push through the gate fires the hook, the
            // daemon registers the run, and the drive loop returns at the
            // parked gate as a decision point (exit 0).
            var code = RunCli(["axi", "run", "--intent", "ship the feature"], out var got, out _);
            Assert.Equal(0, code);
            Assert.Contains("branch: feature", got);
            Assert.Contains("step: review", got);
            Assert.Contains("status: awaiting_approval", got);
            Assert.Contains("summary: 1 blocking issue", got);
            Assert.Contains("awaiting_agent: parked", got);
            // No top-level outcome field: the gate is a decision point (the
            // gate help text mentions `outcome:` inline).
            Assert.DoesNotContain("\noutcome:", got);

            var run = db.GetActiveRun(repo.Id, "feature");
            Assert.NotNull(run);
            Assert.Equal(headSha, run!.HeadSha);

            // Reattaching to the in-flight run needs no --intent and lands on
            // the same gate.
            code = RunCli(["axi", "run"], out got, out _);
            Assert.Equal(0, code);
            Assert.Contains("step: review", got);

            // Scoped abort: resolves the branch's active run from the
            // worktree, no --run id needed.
            code = RunCli(["axi", "abort"], out got, out _);
            Assert.Equal(0, code);
            Assert.Contains("aborted: true", got);
            // Run ids are ULIDs with a leading "01", which the TOON leading-zero
            // rule quotes (toon-go does the same).
            Assert.Contains($"run: \"{run.Id}\"", got);
            Assert.Contains("branch: feature", got);

            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (db.GetRun(run.Id)!.Status != RunStatus.Cancelled)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "run never reached cancelled");
                await Task.Delay(50);
            }
            Assert.Equal(RunCancelReason.AbortedByUser, db.GetRun(run.Id)!.Error);

            // Aborting again is an idempotent no-op.
            code = RunCli(["axi", "abort"], out got, out _);
            Assert.Equal(0, code);
            Assert.Contains("aborted: false", got);
            Assert.Contains("no active run (no-op)", got);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public async Task AxiRunPreflightGuardsRejectBadStartingStates()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);

        GitTestSupport.InitRepo(repoTmp.Path);
        File.WriteAllText(Path.Combine(repoTmp.Path, "a.txt"), "one\n");
        GitTestSupport.Git(repoTmp.Path, "add", "a.txt");
        GitTestSupport.Git(repoTmp.Path, "commit", "-q", "-m", "initial");
        var root = await new GitClient().FindGitRootAsync(repoTmp.Path);
        db.InsertRepoWithId("repo-1", root, "git@github.com:u/r.git", "main");

        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);
        await using var host = await StartDaemonAsync(paths, db, manager);

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            // On the default branch: refused even with an intent.
            var code = RunCli(["axi", "run", "--intent", "goal"], out var got, out _);
            Assert.Equal(1, code);
            // The message carries quotes and a colon, so TOON renders it as a
            // quoted string with escaped inner quotes.
            Assert.Contains("refusing to validate \\\"main\\\": it is the default branch", got);
            Assert.Contains("git switch -c", got);

            GitTestSupport.Git(repoTmp.Path, "checkout", "-q", "-b", "feature");

            // Starting a new run without --intent is a usage error.
            code = RunCli(["axi", "run"], out got, out _);
            Assert.Equal(2, code);
            Assert.Contains("--intent is required to start a run", got);

            // A dirty working tree is refused before any push.
            File.WriteAllText(Path.Combine(repoTmp.Path, "a.txt"), "two\n");
            code = RunCli(["axi", "run", "--intent", "goal"], out got, out _);
            Assert.Equal(1, code);
            Assert.Contains("uncommitted changes in the working tree", got);
            GitTestSupport.Git(repoTmp.Path, "checkout", "-q", "--", "a.txt");

            // An unknown --skip step fails flag validation before touching git.
            code = RunCli(["axi", "run", "--intent", "goal", "--skip", "deploy"], out got, out _);
            Assert.Equal(2, code);
            // The message carries quotes, so TOON escapes them inside the
            // quoted error value.
            Assert.Contains("unknown step \\\"deploy\\\"", got);
            Assert.Contains("Valid steps:", got);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public async Task DriveRunReportsPassedOutcomeWhenRunCompletes()
    {
        using var homeTmp = new TempDir();
        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using var db = Database.Open(paths.Db);
        var repo = db.InsertRepoWithId("repo-1", "/home/user/project", "git@github.com:u/r.git", "main");

        var manager = new RunManager(db, (run, _, _) =>
        {
            db.UpdateRunStatus(run.Id, RunStatus.Running);
            var step = db.InsertStepResult(run.Id, StepName.Review);
            db.UpdateStepStatus(step.Id, StepStatus.Completed);
            db.UpdateRunStatus(run.Id, RunStatus.Completed);
            return Task.CompletedTask;
        });
        await using var host = await StartDaemonAsync(paths, db, manager);
        var runId = await manager.StartRunAsync(repo, "feature", "headsha1", "basesha1");

        using var client = await NoMistakes.Ipc.IpcClient.DialAsync(paths.Socket);
        var progress = new StringWriter();
        var (run, ciReady) = await AxiDrive.DriveRunAsync(
            client, progress, runId, autoApprove: false, ciChecksPassed: null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.False(ciReady);
        var output = AxiDrive.RenderDriveResult(run, ciReady);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("outcome: passed", output.Doc);
        // Liveness stream saw the terminal transition.
        Assert.Contains("run: completed", progress.ToString());
    }

    [Fact]
    public async Task AxiAbortByRunIdWithStoppedDaemonRendersNoOp()
    {
        using var homeTmp = new TempDir();

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            // By-id abort needs no repo and no cwd resolution, and a stopped
            // daemon is a successful no-op.
            var code = RunCli(["axi", "abort", "--run", "run-404"], out var got, out _);
            Assert.Equal(0, code);
            Assert.Contains("aborted: false", got);
            Assert.Contains("run: run-404", got);
            Assert.Contains("daemon not running, so no active run to cancel (no-op)", got);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
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
