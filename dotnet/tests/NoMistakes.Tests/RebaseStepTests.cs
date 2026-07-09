using NoMistakes.Config;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Pipeline;
using NoMistakes.Pipeline.Steps;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the rebase-step data-loss guards from Go's rebase tests reachable with a
/// real git repo: the empty-diff SkipRemaining shortcut, and the
/// bundled-local-default-commits detection (#283) that parks a branch which would
/// silently drag another workstream's unpushed default-branch work into the PR.
/// </summary>
public class RebaseStepTests
{
    private static StepContext Ctx(Database db, Run run, Repo repo, string workDir) => new()
    {
        Ct = CancellationToken.None,
        Run = run,
        Repo = repo,
        WorkDir = workDir,
        Config = new Config.Config(),
        Db = db,
        StepResultId = db.InsertStepResult(run.Id, StepName.Rebase).Id,
    };

    [Fact]
    public async Task EmptyDiffAfterRebase_SkipsRemaining()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var work = tmp.File("work");
        Directory.CreateDirectory(work);
        GitTestSupport.InitRepo(work);
        var baseSha = GitTestSupport.WriteAndCommit(work, "a.txt", "one\n", "base");
        // Branch head == base (no delta), so the branch diff against default is empty.
        GitTestSupport.Git(work, "checkout", "-q", "-b", "feature");

        var repo = db.InsertRepo(work, "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", baseSha, baseSha);

        var outcome = await new RebaseStep().ExecuteAsync(Ctx(db, run, repo, work));

        Assert.True(outcome.SkipRemaining);
    }

    [Fact]
    public async Task BundledLocalDefaultCommits_ParksWithAskUser()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);

        // A bare "origin" holding main at the base.
        var remote = tmp.File("remote");
        Directory.CreateDirectory(remote);
        GitTestSupport.Git(remote, "init", "--bare", "-q");

        // The contributor's working repo: main advances locally past origin/main
        // with an UNPUSHED commit, then a feature branch is cut off that local tip.
        var work = tmp.File("work");
        Directory.CreateDirectory(work);
        GitTestSupport.InitRepo(work);
        GitTestSupport.WriteAndCommit(work, "a.txt", "one\n", "base");
        GitTestSupport.Git(work, "remote", "add", "origin", remote);
        GitTestSupport.Git(work, "push", "-q", "origin", "main");
        // Unpushed local-default commit (another workstream's work).
        GitTestSupport.WriteAndCommit(work, "unrelated.txt", "workstream\n", "unpushed default work");
        GitTestSupport.Git(work, "fetch", "-q", "origin");
        // Feature branch built on the unpushed local tip.
        GitTestSupport.Git(work, "checkout", "-q", "-b", "feature");
        var headSha = GitTestSupport.WriteAndCommit(work, "feature.txt", "feat\n", "feature work");

        var baseSha = GitTestSupport.Git(work, "rev-parse", "origin/main");
        var repo = db.InsertRepo(work, remote, "main");
        // WorkingPath is the contributor working repo the detection reads local main from.
        db.UpdateRepoWorkingPath(repo.Id, work);
        repo = db.GetRepo(repo.Id)!;
        var run = db.InsertRun(repo.Id, "feature", headSha, baseSha);

        var outcome = await new RebaseStep().ExecuteAsync(Ctx(db, run, repo, work));

        Assert.True(outcome.NeedsApproval);
        Assert.False(outcome.AutoFixable);
        var findings = FindingsParser.Parse(outcome.Findings);
        var finding = Assert.Single(findings.Items);
        Assert.Equal(FindingActions.AskUser, finding.Action);
        Assert.Contains("never pushed to origin/main", finding.Description);
    }
}
