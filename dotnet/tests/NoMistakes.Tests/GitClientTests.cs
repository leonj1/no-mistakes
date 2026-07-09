using NoMistakes.Git;
using Xunit;

namespace NoMistakes.Tests;

public class GitClientTests
{
    private static GitClient NewClient() => new();

    [Fact]
    public async Task Run_ReturnsTrimmedStdout()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "first");

        var git = NewClient();
        var head = await git.RunAsync(tmp.Path, "rev-parse", "HEAD");
        Assert.Equal(40, head.Length);
        Assert.DoesNotContain("\n", head);
    }

    [Fact]
    public async Task Run_NonZeroThrowsWithRedactedCommand()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var git = NewClient();
        var ex = await Assert.ThrowsAsync<GitCommandException>(
            () => git.RunAsync(tmp.Path, "rev-parse", "does-not-exist"));
        Assert.Contains("git", ex.Message);
        Assert.NotEqual(0, ex.ExitCode);
    }

    [Fact]
    public async Task InitBare_CreatesBareRepo()
    {
        using var tmp = new TempDir();
        var barePath = tmp.File("gate.git");
        var git = NewClient();
        await git.InitBareAsync(barePath);
        Assert.True(GitClient.IsBareGitDir(barePath));
    }

    [Fact]
    public async Task AddRemoteAndGetUrl_RoundTrips()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var git = NewClient();
        await git.AddRemoteAsync(tmp.Path, "origin", "https://example.com/repo.git");
        var url = await git.GetRemoteUrlAsync(tmp.Path, "origin");
        Assert.Equal("https://example.com/repo.git", url);
    }

    [Fact]
    public async Task EnsureRemote_AddsThenUpdates()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var git = NewClient();
        await git.EnsureRemoteAsync(tmp.Path, "origin", "https://a.example/x.git");
        Assert.Equal("https://a.example/x.git", await git.GetRemoteUrlAsync(tmp.Path, "origin"));
        await git.EnsureRemoteAsync(tmp.Path, "origin", "https://b.example/y.git");
        Assert.Equal("https://b.example/y.git", await git.GetRemoteUrlAsync(tmp.Path, "origin"));
    }

    [Fact]
    public async Task GetRemoteUrl_NotFoundThrows()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var git = NewClient();
        await Assert.ThrowsAsync<GitCommandException>(() => git.GetRemoteUrlAsync(tmp.Path, "nope"));
    }

    [Fact]
    public async Task FindGitRoot_ResolvesRoot()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sub = Directory.CreateDirectory(tmp.File("sub")).FullName;
        var git = NewClient();
        var root = await git.FindGitRootAsync(sub);
        Assert.Equal(GitTestSupport.Git(tmp.Path, "rev-parse", "--show-toplevel"), NormalizePath(root));
    }

    [Fact]
    public async Task FindGitRoot_NotFoundThrows()
    {
        using var tmp = new TempDir();
        var git = NewClient();
        await Assert.ThrowsAsync<GitCommandException>(() => git.FindGitRootAsync(tmp.Path));
    }

    [Fact]
    public async Task DiffNameOnly_ListsChangedFiles()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var b = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var h = GitTestSupport.WriteAndCommit(tmp.Path, "b.txt", "b", "add b");
        var git = NewClient();
        var files = await git.DiffNameOnlyAsync(tmp.Path, b, h);
        Assert.Equal(new[] { "b.txt" }, files);
    }

    [Fact]
    public async Task DiffAgainstEmptyTree_ShowsAllFiles()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var h = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        var files = await git.DiffNameOnlyAsync(tmp.Path, GitClient.EmptyTreeSha, h);
        Assert.Contains("a.txt", files);
    }

    [Fact]
    public async Task HeadShaAndCurrentBranch()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sha = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        Assert.Equal(sha, await git.HeadShaAsync(tmp.Path));
        Assert.Equal("main", await git.CurrentBranchAsync(tmp.Path));
    }

    [Fact]
    public async Task CommitTime_ReturnsUtc()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sha = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        var t = await git.CommitTimeAsync(tmp.Path, sha);
        Assert.Equal(TimeSpan.Zero, t.Offset);
        Assert.True(t.Year >= 2000);
    }

    [Fact]
    public async Task IsDetachedHead_TrueWhenDetached()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sha = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        Assert.False(await git.IsDetachedHeadAsync(tmp.Path));
        GitTestSupport.Git(tmp.Path, "checkout", "-q", sha);
        Assert.True(await git.IsDetachedHeadAsync(tmp.Path));
    }

    [Fact]
    public async Task HasUncommittedChanges_DetectsDirtyTree()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        Assert.False(await git.HasUncommittedChangesAsync(tmp.Path));
        System.IO.File.WriteAllText(tmp.File("a.txt"), "changed");
        Assert.True(await git.HasUncommittedChangesAsync(tmp.Path));
    }

    [Fact]
    public async Task CreateBranch_AndDuplicateFails()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        await git.CreateBranchAsync(tmp.Path, "feature");
        Assert.Equal("feature", await git.CurrentBranchAsync(tmp.Path));
        await Assert.ThrowsAsync<GitCommandException>(() => git.CreateBranchAsync(tmp.Path, "feature"));
    }

    [Fact]
    public async Task CommitAll_CommitsAndFailsWhenClean()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        System.IO.File.WriteAllText(tmp.File("b.txt"), "b");
        var git = NewClient();
        await git.CommitAllAsync(tmp.Path, "add b");
        Assert.False(await git.HasUncommittedChangesAsync(tmp.Path));
        await Assert.ThrowsAsync<GitCommandException>(() => git.CommitAllAsync(tmp.Path, "nothing"));
    }

    [Fact]
    public async Task ResolveRef_And_RefExists()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sha = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        var git = NewClient();
        Assert.Equal(sha, await git.ResolveRefAsync(tmp.Path, "HEAD"));
        Assert.True(await git.RefExistsAsync(tmp.Path, "HEAD"));
        Assert.False(await git.RefExistsAsync(tmp.Path, "refs/heads/missing"));
        await Assert.ThrowsAsync<GitCommandException>(() => git.ResolveRefAsync(tmp.Path, "refs/heads/missing"));
    }

    [Fact]
    public async Task ShowFile_AtHeadAndAbsent()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "hello", "base");
        var git = NewClient();
        Assert.Equal("hello", await git.ShowFileAsync(tmp.Path, "HEAD", "a.txt"));
        await Assert.ThrowsAsync<GitCommandException>(() => git.ShowFileAsync(tmp.Path, "HEAD", "missing.txt"));
    }

    [Fact]
    public async Task CopyLocalUserIdentity_CopiesNameAndEmail()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        GitTestSupport.Git(src.Path, "init", "-q");
        GitTestSupport.Git(src.Path, "config", "user.name", "Alice");
        GitTestSupport.Git(src.Path, "config", "user.email", "alice@example.com");
        GitTestSupport.Git(dst.Path, "init", "-q");
        var git = NewClient();
        await git.CopyLocalUserIdentityAsync(src.Path, dst.Path);
        Assert.Equal("Alice", GitTestSupport.Git(dst.Path, "config", "--get", "user.name"));
        Assert.Equal("alice@example.com", GitTestSupport.Git(dst.Path, "config", "--get", "user.email"));
    }

    [Fact]
    public void IsZeroSha_RecognizesNullRef()
    {
        Assert.True(GitClient.IsZeroSha("0000000000000000000000000000000000000000"));
        Assert.False(GitClient.IsZeroSha("deadbeef"));
    }

    // --- Worktree (acceptance: add/remove matches Go) -------------------------

    [Fact]
    public async Task WorktreeAddAndRemove()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var sha = GitTestSupport.WriteAndCommit(tmp.Path, "a.txt", "a", "base");
        using var wtParent = new TempDir();
        var wtPath = wtParent.File("wt");
        var git = NewClient();
        await git.WorktreeAddAsync(tmp.Path, wtPath, sha);
        Assert.True(System.IO.File.Exists(Path.Combine(wtPath, "a.txt")));
        await git.WorktreeRemoveAsync(tmp.Path, wtPath);
        Assert.False(Directory.Exists(wtPath));
    }

    // --- Push / ls-remote / default branch ------------------------------------

    [Fact]
    public async Task Push_And_LsRemote()
    {
        using var upstream = new TempDir();
        GitTestSupport.Git(upstream.Path, "init", "--bare", "-q");
        using var work = new TempDir();
        GitTestSupport.InitRepo(work.Path);
        var sha = GitTestSupport.WriteAndCommit(work.Path, "a.txt", "a", "base");
        var git = NewClient();
        await git.AddRemoteAsync(work.Path, "origin", upstream.Path);
        await git.PushAsync(work.Path, "origin", "refs/heads/main", "", false);
        Assert.Equal(sha, await git.LsRemoteAsync(work.Path, "origin", "refs/heads/main"));
        Assert.Equal(string.Empty, await git.LsRemoteAsync(work.Path, "origin", "refs/heads/missing"));
    }

    [Fact]
    public async Task DefaultBranch_ReadsRemoteSymref()
    {
        using var upstream = new TempDir();
        GitTestSupport.Git(upstream.Path, "init", "--bare", "-q", "-b", "trunk");
        using var work = new TempDir();
        GitTestSupport.Git(work.Path, "init", "-q");
        GitTestSupport.Git(work.Path, "config", "user.name", "test");
        GitTestSupport.Git(work.Path, "config", "user.email", "test@test.com");
        GitTestSupport.Git(work.Path, "checkout", "-q", "-b", "trunk");
        GitTestSupport.WriteAndCommit(work.Path, "a.txt", "a", "base");
        var git = NewClient();
        await git.AddRemoteAsync(work.Path, "origin", upstream.Path);
        await git.PushAsync(work.Path, "origin", "refs/heads/trunk", "", false);
        Assert.Equal("trunk", await git.DefaultBranchAsync(work.Path, "origin"));
    }

    [Fact]
    public async Task DefaultBranch_FallsBackToMainOnBadRemote()
    {
        using var tmp = new TempDir();
        GitTestSupport.InitRepo(tmp.Path);
        var git = NewClient();
        Assert.Equal("main", await git.DefaultBranchAsync(tmp.Path, "no-such-remote"));
    }

    private static string NormalizePath(string p)
    {
        try
        {
            return new DirectoryInfo(p).ResolveLinkTarget(true)?.FullName ?? new DirectoryInfo(p).FullName;
        }
        catch
        {
            return p;
        }
    }
}
