using NoMistakes.Core;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ported from Go's internal/paths/paths_test.go. Env-var tests live in this one
/// class so they run serially (xunit serializes tests within a class), avoiding
/// races on the process-wide NM_HOME variable.
/// </summary>
public sealed class PathsTests
{
    [Fact]
    public void WithRootExposesDerivedPaths()
    {
        var root = Path.Combine("tmp", "nm-test");
        var p = Paths.WithRoot(root);

        Assert.Equal(root, p.Root);
        Assert.Equal(Path.Combine(root, "state.sqlite"), p.Db);
        Assert.Equal(Path.Combine(root, "socket"), p.Socket);
        Assert.Equal(Path.Combine(root, "daemon.pid"), p.PidFile);
        Assert.Equal(Path.Combine(root, "config.yaml"), p.ConfigFile);
    }

    [Fact]
    public void RepoPaths()
    {
        var root = Path.Combine("tmp", "nm-test");
        var p = Paths.WithRoot(root);

        Assert.Equal(Path.Combine(root, "repos"), p.ReposDir);
        Assert.Equal(Path.Combine(root, "repos", "abc123.git"), p.RepoDir("abc123"));
    }

    [Fact]
    public void WorktreePaths()
    {
        var root = Path.Combine("tmp", "nm-test");
        var p = Paths.WithRoot(root);

        Assert.Equal(Path.Combine(root, "worktrees"), p.WorktreesDir);
        Assert.Equal(Path.Combine(root, "worktrees", "repo1", "run1"), p.WorktreeDir("repo1", "run1"));
    }

    [Fact]
    public void LogPaths()
    {
        var root = Path.Combine("tmp", "nm-test");
        var p = Paths.WithRoot(root);

        Assert.Equal(Path.Combine(root, "logs"), p.LogsDir);
        Assert.Equal(Path.Combine(root, "logs", "run1"), p.RunLogDir("run1"));
        Assert.Equal(Path.Combine(root, "logs", "daemon.log"), p.DaemonLog);
    }

    [Fact]
    public void NewWithEnvOverride()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("NM_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", dir.Path);
            var p = Paths.New();
            Assert.Equal(dir.Path, p.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NM_HOME", old);
        }
    }

    [Fact]
    public void NewDefault()
    {
        var old = Environment.GetEnvironmentVariable("NM_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NM_HOME", null);
            var p = Paths.New();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            }
            Assert.Equal(Path.Combine(home, ".no-mistakes"), p.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NM_HOME", old);
        }
    }

    [Fact]
    public void EnsureDirsCreatesAllDirectories()
    {
        using var dir = new TempDir();
        var p = Paths.WithRoot(Path.Combine(dir.Path, "nm"));

        p.EnsureDirs();

        foreach (var d in new[] { p.Root, p.ReposDir, p.WorktreesDir, p.LogsDir, p.ServerPidsDir })
        {
            Assert.True(Directory.Exists(d), $"expected dir {d} to exist");
        }
    }
}
