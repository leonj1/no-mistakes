using Microsoft.Data.Sqlite;
using NoMistakes.Core;

namespace NoMistakes.Data;

/// <summary>A pipeline run. Mirrors Go's db.Run.</summary>
public sealed class Run
{
    public string Id { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;
    public string Status { get; set; } = RunStatus.Pending;
    public string? PrUrl { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Unix-seconds timestamp at which the run parked at a gate awaiting the
    /// driving agent (an awaiting_approval or fix_review step). Null whenever the
    /// run is not parked: it is set on gate entry and cleared the moment the
    /// agent responds or the wait is cancelled. Observability only; it does not
    /// affect gate resolution.
    /// </summary>
    public long? AwaitingAgentSince { get; set; }

    public string? Intent { get; set; }
    public string? IntentSource { get; set; }
    public string? IntentSessionId { get; set; }
    public double? IntentScore { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>The four intent-related columns persisted on a run. Mirrors Go's db.RunIntent.</summary>
public readonly record struct RunIntent(string Summary, string Source, string SessionId, double Score);

public sealed partial class Database
{
    private const string RunSelectColumns =
        "id, repo_id, branch, head_sha, base_sha, status, pr_url, error, awaiting_agent_since, intent, intent_source, intent_session_id, intent_score, created_at, updated_at";

    /// <summary>Creates a new run record in the pending state.</summary>
    public Run InsertRun(string repoId, string branch, string headSha, string baseSha)
    {
        var ts = Now();
        var run = new Run
        {
            Id = NewId(),
            RepoId = repoId,
            Branch = branch,
            HeadSha = headSha,
            BaseSha = baseSha,
            Status = RunStatus.Pending,
            CreatedAt = ts,
            UpdatedAt = ts,
        };
        lock (gate)
        {
            using var cmd = NewCommand(
                "INSERT INTO runs (id, repo_id, branch, head_sha, base_sha, status, created_at, updated_at) VALUES ($id, $repo, $branch, $head, $base, $status, $created, $updated)");
            Bind(cmd, "$id", run.Id);
            Bind(cmd, "$repo", run.RepoId);
            Bind(cmd, "$branch", run.Branch);
            Bind(cmd, "$head", run.HeadSha);
            Bind(cmd, "$base", run.BaseSha);
            Bind(cmd, "$status", run.Status);
            Bind(cmd, "$created", run.CreatedAt);
            Bind(cmd, "$updated", run.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
        return run;
    }

    /// <summary>Returns a run by ID, or null if absent.</summary>
    public Run? GetRun(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {RunSelectColumns} FROM runs WHERE id = $id");
            Bind(cmd, "$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ScanRun(r) : null;
        }
    }

    /// <summary>Returns all runs for a repo, newest first.</summary>
    public List<Run> GetRunsByRepo(string repoId)
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                $"SELECT {RunSelectColumns} FROM runs WHERE repo_id = $repo ORDER BY created_at DESC, id DESC");
            Bind(cmd, "$repo", repoId);
            return ReadRuns(cmd);
        }
    }

    /// <summary>
    /// Returns the active (pending or running) run for a repo. When
    /// <paramref name="branch"/> is empty, the most recently created active run
    /// on any branch is returned; otherwise only a run on that exact branch
    /// matches (strict, no fallback).
    /// </summary>
    public Run? GetActiveRun(string repoId, string branch)
    {
        lock (gate)
        {
            SqliteCommand cmd;
            if (string.IsNullOrEmpty(branch))
            {
                cmd = NewCommand(
                    $"SELECT {RunSelectColumns} FROM runs WHERE repo_id = $repo AND status IN ('pending', 'running') ORDER BY created_at DESC, id DESC LIMIT 1");
                Bind(cmd, "$repo", repoId);
            }
            else
            {
                cmd = NewCommand(
                    $"SELECT {RunSelectColumns} FROM runs WHERE repo_id = $repo AND branch = $branch AND status IN ('pending', 'running') ORDER BY created_at DESC, id DESC LIMIT 1");
                Bind(cmd, "$repo", repoId);
                Bind(cmd, "$branch", branch);
            }
            using (cmd)
            {
                using var r = cmd.ExecuteReader();
                return r.Read() ? ScanRun(r) : null;
            }
        }
    }

    /// <summary>Returns all pending or running runs across all repos, newest first.</summary>
    public List<Run> GetActiveRuns()
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                $"SELECT {RunSelectColumns} FROM runs WHERE status IN ($pending, $running) ORDER BY created_at DESC, id DESC");
            Bind(cmd, "$pending", RunStatus.Pending);
            Bind(cmd, "$running", RunStatus.Running);
            return ReadRuns(cmd);
        }
    }

    /// <summary>Updates a run's status and updated_at timestamp.</summary>
    public void UpdateRunStatus(string id, string status)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE runs SET status = $status, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$status", status);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the PR URL on a run.</summary>
    public void UpdateRunPrUrl(string id, string prUrl)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE runs SET pr_url = $pr, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$pr", prUrl);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Updates the run head SHA and timestamp.</summary>
    public void UpdateRunHeadSha(string id, string headSha)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE runs SET head_sha = $head, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$head", headSha);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the error message on a run and marks it failed.</summary>
    public void UpdateRunError(string id, string errMsg) =>
        UpdateRunErrorStatus(id, errMsg, RunStatus.Failed);

    /// <summary>Sets the error message and terminal status on a run.</summary>
    public void UpdateRunErrorStatus(string id, string errMsg, string status)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE runs SET error = $err, status = $status, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$err", errMsg);
            Bind(cmd, "$status", status);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Persists the inferred user intent for a run.</summary>
    public void UpdateRunIntent(string id, RunIntent intent)
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                "UPDATE runs SET intent = $intent, intent_source = $source, intent_session_id = $session, intent_score = $score, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$intent", intent.Summary);
            Bind(cmd, "$source", intent.Source);
            Bind(cmd, "$session", intent.SessionId);
            Bind(cmd, "$score", intent.Score);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks a run as parked awaiting the driving agent, stamping
    /// awaiting_agent_since with the current time. A pollable observability
    /// signal only; it does not change gate resolution.
    /// </summary>
    public void SetRunAwaitingAgent(string id)
    {
        lock (gate)
        {
            var ts = Now();
            using var cmd = NewCommand("UPDATE runs SET awaiting_agent_since = $ts, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$ts", ts);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Clears the awaiting-agent marker on a run, called the moment the agent
    /// responds or the approval wait is cancelled so awaiting_agent_since is
    /// non-null exactly while a gate is actually parked.
    /// </summary>
    public void ClearRunAwaitingAgent(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE runs SET awaiting_agent_since = NULL, updated_at = $ts WHERE id = $id");
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks any runs stuck in pending/running as failed and fails any in-progress
    /// steps, clearing the awaiting-agent marker so a recovered run is never
    /// reported as still parked. Called at daemon startup to clean up after a
    /// crash. Returns the number of recovered runs.
    /// </summary>
    public int RecoverStaleRuns(string errMsg)
    {
        lock (gate)
        {
            var ts = Now();
            using var tx = connection.BeginTransaction();

            // Fail stale steps first (running, awaiting_approval, fixing, fix_review).
            using (var stepCmd = connection.CreateCommand())
            {
                stepCmd.Transaction = tx;
                stepCmd.CommandText =
                    "UPDATE step_results SET status = $failed, error = $err, completed_at = $ts WHERE status IN ($running, $awaiting, $fixing, $fixReview)";
                Bind(stepCmd, "$failed", StepStatus.Failed);
                Bind(stepCmd, "$err", errMsg);
                Bind(stepCmd, "$ts", ts);
                Bind(stepCmd, "$running", StepStatus.Running);
                Bind(stepCmd, "$awaiting", StepStatus.AwaitingApproval);
                Bind(stepCmd, "$fixing", StepStatus.Fixing);
                Bind(stepCmd, "$fixReview", StepStatus.FixReview);
                stepCmd.ExecuteNonQuery();
            }

            // Fail stale runs and drop any awaiting-agent marker.
            int count;
            using (var runCmd = connection.CreateCommand())
            {
                runCmd.Transaction = tx;
                runCmd.CommandText =
                    "UPDATE runs SET status = $failed, error = $err, awaiting_agent_since = NULL, updated_at = $ts WHERE status IN ($pending, $running)";
                Bind(runCmd, "$failed", RunStatus.Failed);
                Bind(runCmd, "$err", errMsg);
                Bind(runCmd, "$ts", ts);
                Bind(runCmd, "$pending", RunStatus.Pending);
                Bind(runCmd, "$running", RunStatus.Running);
                count = runCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return count;
        }
    }

    private static List<Run> ReadRuns(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var runs = new List<Run>();
        while (r.Read())
        {
            runs.Add(ScanRun(r));
        }
        return runs;
    }

    private static Run ScanRun(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        RepoId = r.GetString(1),
        Branch = r.GetString(2),
        HeadSha = r.GetString(3),
        BaseSha = r.GetString(4),
        Status = r.GetString(5),
        PrUrl = GetNullableString(r, 6),
        Error = GetNullableString(r, 7),
        AwaitingAgentSince = GetNullableLong(r, 8),
        Intent = GetNullableString(r, 9),
        IntentSource = GetNullableString(r, 10),
        IntentSessionId = GetNullableString(r, 11),
        IntentScore = GetNullableDouble(r, 12),
        CreatedAt = r.GetInt64(13),
        UpdatedAt = r.GetInt64(14),
    };
}
