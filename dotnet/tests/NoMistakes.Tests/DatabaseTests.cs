using Microsoft.Data.Sqlite;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ported from Go's internal/db/db_test.go: open/close, schema creation, and the
/// additive migration paths for legacy databases.
/// </summary>
public sealed class DatabaseTests
{
    [Fact]
    public void OpenAndClose()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        Assert.NotNull(db);
    }

    [Fact]
    public void OpenCreatesSchema()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");
        using (Database.Open(path))
        {
        }

        Assert.True(DataTestSupport.TableExists(path, "repos"));
        Assert.True(DataTestSupport.TableExists(path, "runs"));
        Assert.True(DataTestSupport.TableExists(path, "step_results"));
        Assert.True(DataTestSupport.HasColumn(path, "repos", "fork_url"));
    }

    [Fact]
    public void OpenCreatesStepRoundsTable()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");
        using (Database.Open(path))
        {
        }

        Assert.True(DataTestSupport.TableExists(path, "step_rounds"));
    }

    [Fact]
    public void OpenMigratesExistingStepRoundsColumns()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");

        using (var legacy = DataTestSupport.OpenRaw(path))
        {
            DataTestSupport.Exec(legacy, @"
                CREATE TABLE step_rounds (
                    id TEXT PRIMARY KEY,
                    step_result_id TEXT NOT NULL,
                    round INTEGER NOT NULL,
                    trigger_type TEXT NOT NULL,
                    findings_json TEXT,
                    duration_ms INTEGER NOT NULL,
                    created_at INTEGER NOT NULL
                );");
        }

        using (Database.Open(path))
        {
        }

        foreach (var column in new[] { "selected_finding_ids", "selection_source", "fix_summary", "user_findings_json" })
        {
            Assert.True(DataTestSupport.HasColumn(path, "step_rounds", column), $"expected migrated column {column}");
        }
    }

    [Fact]
    public void OpenMigratesReposForkUrlColumn()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");

        using (var legacy = DataTestSupport.OpenRaw(path))
        {
            DataTestSupport.Exec(legacy, @"
                CREATE TABLE repos (
                    id TEXT PRIMARY KEY,
                    working_path TEXT NOT NULL UNIQUE,
                    upstream_url TEXT NOT NULL,
                    default_branch TEXT NOT NULL DEFAULT 'main',
                    created_at INTEGER NOT NULL
                );");
            DataTestSupport.Exec(legacy, @"
                INSERT INTO repos (id, working_path, upstream_url, default_branch, created_at)
                VALUES ('repo-1', '/work/repo', 'git@github.com:parent/repo.git', 'main', 123);");
        }

        using var db = Database.Open(path);

        Assert.True(DataTestSupport.HasColumn(path, "repos", "fork_url"));
        var repo = db.GetRepo("repo-1");
        Assert.NotNull(repo);
        Assert.Equal(string.Empty, repo!.ForkUrl);

        var updated = db.UpdateRepoForkUrl(repo.Id, "git@github.com:fork/repo.git");
        Assert.NotNull(updated);
        Assert.Equal("git@github.com:fork/repo.git", updated!.ForkUrl);
    }

    [Fact]
    public void OpenWaitsForTransientMigrationLock()
    {
        using var dir = new TempDir();
        var path = dir.File("test.sqlite");

        using var locker = DataTestSupport.OpenRaw(path);
        DataTestSupport.Exec(locker, "BEGIN EXCLUSIVE");

        // A raw thread (not a Task) drives Open so the wait/complete handshake
        // avoids blocking Task operations the xUnit analyzers forbid.
        using var completed = new System.Threading.ManualResetEventSlim(false);
        Exception? openError = null;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                using var db = Database.Open(path);
            }
            catch (Exception ex)
            {
                openError = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        worker.Start();

        // Open must not complete while the exclusive lock is held.
        Assert.False(completed.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken), "Open returned before the migration lock was released");

        DataTestSupport.Exec(locker, "COMMIT");

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "Open did not finish after the migration lock was released");
        worker.Join();
        Assert.Null(openError);
    }
}
