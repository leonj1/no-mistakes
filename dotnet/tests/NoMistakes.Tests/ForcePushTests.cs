using NoMistakes.Pipeline.Steps;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's TestResolveForcePushDecision_* — the force-push data-loss guard.
/// Uses a real local repo whose origin is a bare remote, exercising the guard
/// through the GitRunner seam exactly as the push/CI steps do. The invariant:
/// a force-push that would discard commits the pipeline never incorporated must
/// be refused, never silently applied.
/// </summary>
public class ForcePushTests
{
    private sealed record Fixture(string Dir, GitRunner GitRun, string Remote, string FeatureSha);

    // Builds a local repo whose origin points at a bare remote, with main + a
    // feature branch pushed. Mirrors Go's newForcePushFixture.
    private static Fixture NewFixture(TempDir tmp)
    {
        var remote = tmp.File("remote");
        Directory.CreateDirectory(remote);
        GitTestSupport.Git(remote, "init", "--bare", "-q");

        var dir = tmp.File("local");
        Directory.CreateDirectory(dir);
        GitTestSupport.InitRepo(dir);
        GitTestSupport.WriteAndCommit(dir, "base.txt", "base", "base");
        GitTestSupport.Git(dir, "remote", "add", "origin", remote);
        GitTestSupport.Git(dir, "push", "-q", "origin", "main");

        GitTestSupport.Git(dir, "checkout", "-q", "-b", "feature");
        var featureSha = GitTestSupport.WriteAndCommit(dir, "feature.txt", "feature", "feature");
        GitTestSupport.Git(dir, "push", "-q", "origin", "feature");

        GitRunner gitRun = args => Task.FromResult(GitTestSupport.Git(dir, args));
        return new Fixture(dir, gitRun, remote, featureSha);
    }

    [Fact]
    public async Task NewBranch()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);
        var d = await ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/does-not-exist", "deadbeef", "", "");
        Assert.True(d.NewBranch);
    }

    [Fact]
    public async Task UpToDate()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);
        var d = await ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", f.FeatureSha, f.FeatureSha, "");
        Assert.True(d.UpToDate);
    }

    [Fact]
    public async Task RemoteUnchangedSinceLastSeen_GuardedForcePush()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);
        // New local head not yet on the remote (e.g. a rebase result).
        var newHead = GitTestSupport.WriteAndCommit(f.Dir, "more.txt", "more", "more");

        var d = await ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", newHead, f.FeatureSha, "");
        Assert.False(d.NewBranch);
        Assert.False(d.UpToDate);
        Assert.Equal(f.FeatureSha, d.RemoteSha);
    }

    [Fact]
    public async Task RefusesUnincorporatedRemoteCommit()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);

        // Out-of-band: the remote feature advances with a commit we never saw.
        var other = tmp.File("other");
        Directory.CreateDirectory(other);
        GitTestSupport.Git(other, "clone", "-q", f.Remote, ".");
        GitTestSupport.Git(other, "config", "user.name", "o");
        GitTestSupport.Git(other, "config", "user.email", "o@test.com");
        GitTestSupport.Git(other, "checkout", "-q", "feature");
        GitTestSupport.WriteAndCommit(other, "out_of_band.txt", "unseen", "out of band");
        GitTestSupport.Git(other, "push", "-q", "origin", "feature");

        // Our local head descends from the OLD feature tip, not the new remote tip.
        var newHead = GitTestSupport.WriteAndCommit(f.Dir, "local.txt", "local", "local");

        // lastSeen is the OLD tip (stale): the remote moved past it out of band.
        await Assert.ThrowsAsync<ForcePushWouldDiscardException>(() =>
            ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", newHead, f.FeatureSha, ""));
    }

    [Fact]
    public async Task AllowsWhenRemoteContentIncorporated()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);

        var other = tmp.File("other");
        Directory.CreateDirectory(other);
        GitTestSupport.Git(other, "clone", "-q", f.Remote, ".");
        GitTestSupport.Git(other, "config", "user.name", "o");
        GitTestSupport.Git(other, "config", "user.email", "o@test.com");
        GitTestSupport.Git(other, "checkout", "-q", "feature");
        var remoteTip = GitTestSupport.WriteAndCommit(other, "shared.txt", "shared work", "shared work");
        GitTestSupport.Git(other, "push", "-q", "origin", "feature");

        // Our local head actually contains that remote commit (fetched + built on).
        GitTestSupport.Git(f.Dir, "fetch", "-q", f.Remote, "feature");
        GitTestSupport.Git(f.Dir, "reset", "-q", "--hard", remoteTip);
        var newHead = GitTestSupport.WriteAndCommit(f.Dir, "extra.txt", "extra", "extra on top");

        var d = await ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", newHead, f.FeatureSha, "");
        Assert.False(d.NewBranch);
        Assert.False(d.UpToDate);
        Assert.Equal(remoteTip, d.RemoteSha);
    }

    [Fact]
    public async Task AllowsRewriteOfKnownBaseHistory()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);
        var mainSha = GitTestSupport.Git(f.Dir, "rev-parse", "main");

        // Rewrite feature: drop the original commit, add a different one. The remote
        // still holds the original (featureSha), which the rewrite drops on purpose.
        GitTestSupport.Git(f.Dir, "reset", "-q", "--hard", mainSha);
        var newHead = GitTestSupport.WriteAndCommit(f.Dir, "feature.txt", "rewritten", "rewritten feature");

        // lastSeen empty to force the content check; baseSha is the prior branch tip
        // the user is knowingly rewriting -> the dropped original is not data loss.
        var d = await ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", newHead, "", f.FeatureSha);
        Assert.False(d.NewBranch);
        Assert.False(d.UpToDate);
        Assert.Equal(f.FeatureSha, d.RemoteSha);
    }

    [Fact]
    public async Task RefusesOutOfBandEvenWithBase()
    {
        using var tmp = new TempDir();
        var f = NewFixture(tmp);
        var mainSha = GitTestSupport.Git(f.Dir, "rev-parse", "main");

        // Out-of-band commit lands on origin/feature on top of featureSha.
        var other = tmp.File("other");
        Directory.CreateDirectory(other);
        GitTestSupport.Git(other, "clone", "-q", f.Remote, ".");
        GitTestSupport.Git(other, "config", "user.name", "o");
        GitTestSupport.Git(other, "config", "user.email", "o@test.com");
        GitTestSupport.Git(other, "checkout", "-q", "feature");
        GitTestSupport.WriteAndCommit(other, "out_of_band.txt", "unseen", "out of band");
        GitTestSupport.Git(other, "push", "-q", "origin", "feature");

        // User rewrites feature off the base; the rewrite contains neither the
        // original commit nor the out-of-band one.
        GitTestSupport.Git(f.Dir, "reset", "-q", "--hard", mainSha);
        var newHead = GitTestSupport.WriteAndCommit(f.Dir, "feature.txt", "rewritten", "rewritten feature");

        // baseSha covers the original commit; the out-of-band commit is a descendant
        // of it, not an ancestor, so it stays flagged.
        await Assert.ThrowsAsync<ForcePushWouldDiscardException>(() =>
            ForcePush.ResolveDecisionAsync(f.GitRun, f.Remote, "refs/heads/feature", newHead, f.FeatureSha, f.FeatureSha));
    }
}
