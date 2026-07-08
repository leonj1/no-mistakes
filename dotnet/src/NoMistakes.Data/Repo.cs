using Microsoft.Data.Sqlite;

namespace NoMistakes.Data;

/// <summary>A registered repository. Mirrors Go's db.Repo.</summary>
public sealed class Repo
{
    public string Id { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public string UpstreamUrl { get; set; } = string.Empty;
    public string ForkUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = string.Empty;
    public long CreatedAt { get; set; }

    /// <summary>Returns the remote URL that should receive branch updates.</summary>
    public string PushUrl() =>
        string.IsNullOrEmpty(ForkUrl.Trim()) ? UpstreamUrl : ForkUrl;
}

public sealed partial class Database
{
    private const string RepoSelectColumns =
        "id, working_path, upstream_url, COALESCE(fork_url, ''), default_branch, created_at";

    /// <summary>Creates a new repo record with a caller-provided ID.</summary>
    public Repo InsertRepoWithId(string id, string workingPath, string upstreamUrl, string defaultBranch) =>
        InsertRepoWithIdAndFork(id, workingPath, upstreamUrl, "", defaultBranch);

    /// <summary>Creates a repo record with a caller-provided ID and optional fork push URL.</summary>
    public Repo InsertRepoWithIdAndFork(string id, string workingPath, string upstreamUrl, string forkUrl, string defaultBranch)
    {
        var repo = new Repo
        {
            Id = id,
            WorkingPath = workingPath,
            UpstreamUrl = upstreamUrl,
            ForkUrl = forkUrl.Trim(),
            DefaultBranch = defaultBranch,
            CreatedAt = Now(),
        };
        InsertRepoRow(repo);
        return repo;
    }

    /// <summary>Creates a new repo record with a generated ID.</summary>
    public Repo InsertRepo(string workingPath, string upstreamUrl, string defaultBranch) =>
        InsertRepoWithFork(workingPath, upstreamUrl, "", defaultBranch);

    /// <summary>Creates a new repo record with a generated ID and optional fork push URL.</summary>
    public Repo InsertRepoWithFork(string workingPath, string upstreamUrl, string forkUrl, string defaultBranch)
    {
        var repo = new Repo
        {
            Id = NewId(),
            WorkingPath = workingPath,
            UpstreamUrl = upstreamUrl,
            ForkUrl = forkUrl.Trim(),
            DefaultBranch = defaultBranch,
            CreatedAt = Now(),
        };
        InsertRepoRow(repo);
        return repo;
    }

    private void InsertRepoRow(Repo repo)
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                "INSERT INTO repos (id, working_path, upstream_url, fork_url, default_branch, created_at) VALUES ($id, $wp, $up, $fork, $branch, $created)");
            Bind(cmd, "$id", repo.Id);
            Bind(cmd, "$wp", repo.WorkingPath);
            Bind(cmd, "$up", repo.UpstreamUrl);
            Bind(cmd, "$fork", NullableString(repo.ForkUrl));
            Bind(cmd, "$branch", repo.DefaultBranch);
            Bind(cmd, "$created", repo.CreatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Returns a repo by ID, or null if absent.</summary>
    public Repo? GetRepo(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {RepoSelectColumns} FROM repos WHERE id = $id");
            Bind(cmd, "$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ScanRepo(r) : null;
        }
    }

    /// <summary>Returns a repo by its working path, or null if absent.</summary>
    public Repo? GetRepoByPath(string workingPath)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {RepoSelectColumns} FROM repos WHERE working_path = $wp");
            Bind(cmd, "$wp", workingPath);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ScanRepo(r) : null;
        }
    }

    /// <summary>Refreshes mutable metadata, preserving the ID, created_at, and fork URL.</summary>
    public Repo? UpdateRepoMetadata(string id, string upstreamUrl, string defaultBranch)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE repos SET upstream_url = $up, default_branch = $branch WHERE id = $id");
            Bind(cmd, "$up", upstreamUrl);
            Bind(cmd, "$branch", defaultBranch);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        return GetRepo(id);
    }

    /// <summary>Refreshes metadata and explicitly sets the optional fork push URL.</summary>
    public Repo? UpdateRepoMetadataWithFork(string id, string upstreamUrl, string forkUrl, string defaultBranch)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE repos SET upstream_url = $up, fork_url = $fork, default_branch = $branch WHERE id = $id");
            Bind(cmd, "$up", upstreamUrl);
            Bind(cmd, "$fork", NullableString(forkUrl));
            Bind(cmd, "$branch", defaultBranch);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        return GetRepo(id);
    }

    /// <summary>Sets or clears the optional fork push URL.</summary>
    public Repo? UpdateRepoForkUrl(string id, string forkUrl)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE repos SET fork_url = $fork WHERE id = $id");
            Bind(cmd, "$fork", NullableString(forkUrl));
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        return GetRepo(id);
    }

    /// <summary>Moves a repo record to a new working path, preserving its ID and history.</summary>
    public Repo? UpdateRepoWorkingPath(string id, string workingPath)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE repos SET working_path = $wp WHERE id = $id");
            Bind(cmd, "$wp", workingPath);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        return GetRepo(id);
    }

    /// <summary>Deletes a repo by ID (cascade deletes its runs, steps, and rounds).</summary>
    public void DeleteRepo(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand("DELETE FROM repos WHERE id = $id");
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private List<Repo> GetReposOrdered()
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {RepoSelectColumns} FROM repos ORDER BY working_path");
            using var r = cmd.ExecuteReader();
            var repos = new List<Repo>();
            while (r.Read())
            {
                repos.Add(ScanRepo(r));
            }
            return repos;
        }
    }

    private static Repo ScanRepo(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkingPath = r.GetString(1),
        UpstreamUrl = r.GetString(2),
        ForkUrl = r.GetString(3),
        DefaultBranch = r.GetString(4),
        CreatedAt = r.GetInt64(5),
    };
}
