using Microsoft.Data.Sqlite;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>Ported from Go's internal/db/repo_test.go.</summary>
public sealed class RepoTests
{
    [Fact]
    public void RepoInsertAndGet()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        Assert.NotEqual(string.Empty, repo.Id);
        Assert.Equal("/home/user/project", repo.WorkingPath);
        Assert.Equal("git@github.com:user/project.git", repo.UpstreamUrl);
        Assert.Equal(string.Empty, repo.ForkUrl);
        Assert.Equal(repo.UpstreamUrl, repo.PushUrl());
        Assert.Equal("main", repo.DefaultBranch);
        Assert.NotEqual(0, repo.CreatedAt);

        var got = db.GetRepo(repo.Id);
        Assert.NotNull(got);
        Assert.Equal(repo.Id, got!.Id);
        Assert.Equal(string.Empty, got.ForkUrl);
    }

    [Fact]
    public void RepoForkUrlRoundTrip()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepoWithFork("/home/user/project", "git@github.com:parent/project.git", "git@github.com:fork/project.git", "main");
        Assert.Equal("git@github.com:fork/project.git", repo.ForkUrl);
        Assert.Equal(repo.ForkUrl, repo.PushUrl());

        var got = db.GetRepo(repo.Id);
        Assert.NotNull(got);
        Assert.Equal("git@github.com:parent/project.git", got!.UpstreamUrl);
        Assert.Equal("git@github.com:fork/project.git", got.ForkUrl);
        Assert.Equal("git@github.com:fork/project.git", got.PushUrl());

        var cleared = db.UpdateRepoForkUrl(repo.Id, "");
        Assert.NotNull(cleared);
        Assert.Equal(string.Empty, cleared!.ForkUrl);
        Assert.Equal(cleared.UpstreamUrl, cleared.PushUrl());
    }

    [Fact]
    public void InsertRepoWithId()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepoWithId("custom-id-123", "/home/user/myproject", "git@github.com:user/myproject.git", "develop");

        Assert.Equal("custom-id-123", repo.Id);
        Assert.Equal("/home/user/myproject", repo.WorkingPath);
        Assert.Equal("git@github.com:user/myproject.git", repo.UpstreamUrl);
        Assert.Equal("develop", repo.DefaultBranch);
        Assert.NotEqual(0, repo.CreatedAt);

        var got = db.GetRepo("custom-id-123");
        Assert.NotNull(got);
        Assert.Equal("custom-id-123", got!.Id);
        Assert.Equal("develop", got.DefaultBranch);
    }

    [Fact]
    public void InsertRepoWithIdDuplicate()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        db.InsertRepoWithId("dup-id", "/path/a", "git@github.com:a/b.git", "main");
        Assert.Throws<SqliteException>(() =>
            db.InsertRepoWithId("dup-id", "/path/b", "git@github.com:c/d.git", "main"));
    }

    [Fact]
    public void RepoGetByPath()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var got = db.GetRepoByPath("/home/user/project");
        Assert.NotNull(got);
        Assert.Equal(repo.Id, got!.Id);

        Assert.Null(db.GetRepoByPath("/nonexistent"));
    }

    [Fact]
    public void RepoGetNotFound()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        Assert.Null(db.GetRepo("nonexistent"));
    }

    [Fact]
    public void RepoUniqueWorkingPath()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        db.InsertRepo("/home/user/project", "git@github.com:a/b.git", "main");
        Assert.Throws<SqliteException>(() =>
            db.InsertRepo("/home/user/project", "git@github.com:c/d.git", "main"));
    }

    [Fact]
    public void RepoDelete()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        db.DeleteRepo(repo.Id);
        Assert.Null(db.GetRepo(repo.Id));
    }
}
