using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Git;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// The read-only axi commands driven end to end through CliApp: environment
/// resolution (NM_HOME paths, cwd repo lookup, current branch) plus dispatch.
/// The home test is the full port of Go's
/// TestAxiHomeStartsCurrentBranchWhenOtherBranchIsActive. In the "daemon"
/// collection because it mutates NM_HOME and the process working directory.
/// </summary>
[Collection("daemon")]
public sealed class AxiQueryCliTests
{
    private static int RunCli(string[] args, out string stdoutText, out string stderrText)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = new CliApp(stdout, stderr).Run(args);
        stdoutText = stdout.ToString();
        stderrText = stderr.ToString();
        return code;
    }

    [Fact]
    public async Task AxiHomeShowsOtherBranchActiveRunWithoutActingOnIt()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        GitTestSupport.InitRepo(repoTmp.Path);
        GitTestSupport.Git(repoTmp.Path, "commit", "--allow-empty", "-m", "initial");
        GitTestSupport.Git(repoTmp.Path, "checkout", "-q", "-b", "feature/current");
        // Register the repo under the same symlink-resolved root FindGitRoot
        // reports (e.g. /var vs /private/var on macOS).
        var root = await new GitClient().FindGitRootAsync(repoTmp.Path);

        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        using (var db = Database.Open(paths.Db))
        {
            var repo = db.InsertRepoWithId("repo-1", root, "origin", "main");
            var other = db.InsertRun(repo.Id, "feature/other", "head-other", "base");
            db.UpdateRunStatus(other.Id, RunStatus.Running);
            var step = db.InsertStepResult(other.Id, StepName.Review);
            db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
            db.SetStepFindings(step.Id, """{"findings":[],"summary":"other branch gate"}""");
        }

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(["axi"], out var got, out var errText);

            Assert.Equal(0, code);
            Assert.Equal("", errText);
            foreach (var want in new[]
                     {
                         "current_branch: feature/current",
                         "daemon: stopped",
                         "other_branch_active_run:",
                         "branch: feature/other",
                         "no-mistakes axi run --intent",
                     })
            {
                Assert.Contains(want, got);
            }
            foreach (var forbidden in new[]
                     {
                         "\nactive_run:",
                         "gate:",
                         "no-mistakes axi respond --action approve",
                         "no-mistakes axi abort",
                     })
            {
                Assert.DoesNotContain(forbidden, got);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public async Task AxiStatusReadsRunForCurrentBranch()
    {
        using var repoTmp = new TempDir();
        using var homeTmp = new TempDir();
        GitTestSupport.InitRepo(repoTmp.Path);
        GitTestSupport.Git(repoTmp.Path, "commit", "--allow-empty", "-m", "initial");
        GitTestSupport.Git(repoTmp.Path, "checkout", "-q", "-b", "feature/current");
        var root = await new GitClient().FindGitRootAsync(repoTmp.Path);

        var paths = Paths.WithRoot(homeTmp.Path);
        paths.EnsureDirs();
        string runId;
        using (var db = Database.Open(paths.Db))
        {
            var repo = db.InsertRepoWithId("repo-1", root, "origin", "main");
            var run = db.InsertRun(repo.Id, "feature/current", "abcdef1234567890", "base");
            db.UpdateRunStatus(run.Id, RunStatus.Completed);
            runId = run.Id;
        }

        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", homeTmp.Path);
            Directory.SetCurrentDirectory(root);

            var code = RunCli(["axi", "status"], out var got, out _);

            Assert.Equal(0, code);
            Assert.Contains("run:\n  id: \"" + runId + "\"\n", got);
            Assert.Contains("outcome: passed", got);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
    }

    [Fact]
    public void AxiLogsValidatesStepBeforeTouchingAnyEnvironment()
    {
        // No NM_HOME, no repo, no db: validation must fail first with exit 2.
        var code = RunCli(["axi", "logs"], out var got, out _);
        Assert.Equal(2, code);
        Assert.Contains("error: \"--step is required\"", got);
        Assert.Contains("Valid steps: intent, rebase, review, test, document, lint, push, pr, ci", got);

        code = RunCli(["axi", "logs", "--step", "bogus"], out got, out _);
        Assert.Equal(2, code);
        Assert.Contains("unknown step \\\"bogus\\\"", got);
    }

    [Fact]
    public void AxiUnknownSubcommandFailsUsage()
    {
        var code = RunCli(["axi", "frobnicate"], out var got, out var errText);
        Assert.Equal(2, code);
        Assert.Equal("", got);
        Assert.Contains("unknown command: axi frobnicate", errText);
    }
}
