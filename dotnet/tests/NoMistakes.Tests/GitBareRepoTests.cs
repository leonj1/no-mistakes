using NoMistakes.Git;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Proves the wrapper names a bare gate repo explicitly (via --git-dir) so it
/// works under safe.bareRepository=explicit, which agent harnesses and hardened
/// CI inject. Mirrors the Go regression tests for issue #362.
///
/// These tests mutate process-global git-config env vars, so they are not run
/// in parallel with each other or with tests that also read those vars.
/// </summary>
[Collection("safe-bare-repository")]
public class GitBareRepoTests
{
    [Fact]
    public async Task Run_OnBareRepoUnderSafeBareRepositoryExplicit()
    {
        using var _ = new SafeBareRepositoryExplicit();
        using var tmp = new TempDir();
        var bare = tmp.File("gate.git");
        var git = new GitClient();

        await git.InitBareAsync(bare);

        // A config write + read must work when the bare repo is named explicitly.
        await git.RunAsync(bare, "config", "receive.advertisePushOptions", "true");
        var got = await git.RunAsync(bare, "config", "--get", "receive.advertisePushOptions");
        Assert.Equal("true", got);

        // A working repo must keep using normal cwd discovery.
        using var work = new TempDir();
        GitTestSupport.InitRepo(work.Path);
        var inside = await git.RunAsync(work.Path, "rev-parse", "--is-inside-work-tree");
        Assert.Equal("true", inside);
    }

    [Fact]
    public async Task WorktreeAddRemove_OnBareRepoUnderSafeBareRepositoryExplicit()
    {
        using var _ = new SafeBareRepositoryExplicit();
        using var tmp = new TempDir();
        var bare = tmp.File("gate.git");
        var git = new GitClient();
        await git.InitBareAsync(bare);

        // Seed the bare with a commit by pushing from a working clone.
        using var seed = new TempDir();
        GitTestSupport.InitRepo(seed.Path);
        var sha = GitTestSupport.WriteAndCommit(seed.Path, "a.txt", "a", "base");
        await git.AddRemoteAsync(seed.Path, "origin", bare);
        await git.PushAsync(seed.Path, "origin", "refs/heads/main", "", false);

        using var wtParent = new TempDir();
        var wtPath = wtParent.File("wt");
        await git.WorktreeAddAsync(bare, wtPath, sha);
        Assert.True(System.IO.File.Exists(Path.Combine(wtPath, "a.txt")));
        await git.WorktreeRemoveAsync(bare, wtPath);
        Assert.False(Directory.Exists(wtPath));
    }
}

/// <summary>
/// Sets the git-config injection env vars that reproduce
/// safe.bareRepository=explicit, mirroring how agent harnesses inject config,
/// and restores the previous values on dispose.
/// </summary>
internal sealed class SafeBareRepositoryExplicit : IDisposable
{
    private readonly (string Key, string? Prev)[] _saved;

    public SafeBareRepositoryExplicit()
    {
        var pairs = new (string Key, string Value)[]
        {
            ("GIT_CONFIG_COUNT", "1"),
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"),
        };
        _saved = new (string, string?)[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            _saved[i] = (pairs[i].Key, Environment.GetEnvironmentVariable(pairs[i].Key));
            Environment.SetEnvironmentVariable(pairs[i].Key, pairs[i].Value);
        }
    }

    public void Dispose()
    {
        foreach (var (key, prev) in _saved)
        {
            Environment.SetEnvironmentVariable(key, prev);
        }
    }
}

[CollectionDefinition("safe-bare-repository", DisableParallelization = true)]
public class SafeBareRepositoryCollection
{
}
