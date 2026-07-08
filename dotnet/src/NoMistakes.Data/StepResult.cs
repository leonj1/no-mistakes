using Microsoft.Data.Sqlite;
using NoMistakes.Core;

namespace NoMistakes.Data;

/// <summary>The result of a pipeline step execution. Mirrors Go's db.StepResult.</summary>
public sealed class StepResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public string Status { get; set; } = StepStatus.Pending;
    public int? ExitCode { get; set; }
    public long? DurationMs { get; set; }
    public string? LogPath { get; set; }
    public string? FindingsJson { get; set; }
    public string? Error { get; set; }
    public long? StartedAt { get; set; }
    public long? CompletedAt { get; set; }
}

public sealed partial class Database
{
    private const string StepSelectColumns =
        "id, run_id, step_name, step_order, status, exit_code, duration_ms, log_path, findings_json, error, started_at, completed_at";

    /// <summary>Creates a new step result record in the pending state.</summary>
    public StepResult InsertStepResult(string runId, string stepName)
    {
        var normalized = Core.StepName.Normalize(stepName);
        var step = new StepResult
        {
            Id = NewId(),
            RunId = runId,
            StepName = normalized,
            StepOrder = Core.StepName.Order(normalized),
            Status = StepStatus.Pending,
        };
        lock (gate)
        {
            using var cmd = NewCommand(
                "INSERT INTO step_results (id, run_id, step_name, step_order, status) VALUES ($id, $run, $name, $order, $status)");
            Bind(cmd, "$id", step.Id);
            Bind(cmd, "$run", step.RunId);
            Bind(cmd, "$name", step.StepName);
            Bind(cmd, "$order", step.StepOrder);
            Bind(cmd, "$status", step.Status);
            cmd.ExecuteNonQuery();
        }
        return step;
    }

    /// <summary>Returns a step result by ID, or null if absent.</summary>
    public StepResult? GetStepResult(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {StepSelectColumns} FROM step_results WHERE id = $id");
            Bind(cmd, "$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ScanStep(r) : null;
        }
    }

    /// <summary>Returns all step results for a run, in execution order.</summary>
    public List<StepResult> GetStepsByRun(string runId)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {StepSelectColumns} FROM step_results WHERE run_id = $run ORDER BY step_order");
            Bind(cmd, "$run", runId);
            using var r = cmd.ExecuteReader();
            var steps = new List<StepResult>();
            while (r.Read())
            {
                steps.Add(ScanStep(r));
            }
            return steps;
        }
    }

    /// <summary>Updates a step's status.</summary>
    public void UpdateStepStatus(string id, string status)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET status = $status WHERE id = $id");
            Bind(cmd, "$status", status);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Updates a step's status and execution duration together.</summary>
    public void UpdateStepStatusWithDuration(string id, string status, long durationMs)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET status = $status, duration_ms = $dur WHERE id = $id");
            Bind(cmd, "$status", status);
            Bind(cmd, "$dur", durationMs);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Marks a step as running with a started_at timestamp.</summary>
    public void StartStep(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET status = $status, started_at = $ts WHERE id = $id");
            Bind(cmd, "$status", StepStatus.Running);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Marks a step as completed with timing and result info.</summary>
    public void CompleteStep(string id, int exitCode, long durationMs, string logPath) =>
        CompleteStepWithStatus(id, StepStatus.Completed, exitCode, durationMs, logPath);

    /// <summary>Marks a step as finished with the given status, timing, and result info.</summary>
    public void CompleteStepWithStatus(string id, string status, int exitCode, long durationMs, string logPath)
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                "UPDATE step_results SET status = $status, exit_code = $exit, duration_ms = $dur, log_path = $log, completed_at = $ts WHERE id = $id");
            Bind(cmd, "$status", status);
            Bind(cmd, "$exit", exitCode);
            Bind(cmd, "$dur", durationMs);
            Bind(cmd, "$log", logPath);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Marks a step as failed with an error message and duration.</summary>
    public void FailStep(string id, string errMsg, long durationMs)
    {
        lock (gate)
        {
            using var cmd = NewCommand(
                "UPDATE step_results SET status = $status, error = $err, duration_ms = $dur, completed_at = $ts WHERE id = $id");
            Bind(cmd, "$status", StepStatus.Failed);
            Bind(cmd, "$err", errMsg);
            Bind(cmd, "$dur", durationMs);
            Bind(cmd, "$ts", Now());
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the execution-only duration on a step result.</summary>
    public void SetStepDuration(string id, long durationMs)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET duration_ms = $dur WHERE id = $id");
            Bind(cmd, "$dur", durationMs);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the findings JSON on a step result.</summary>
    public void SetStepFindings(string id, string findingsJson)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET findings_json = $findings WHERE id = $id");
            Bind(cmd, "$findings", findingsJson);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes any stored findings JSON from a step result.</summary>
    public void ClearStepFindings(string id)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_results SET findings_json = NULL WHERE id = $id");
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private static StepResult ScanStep(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        RunId = r.GetString(1),
        StepName = Core.StepName.Normalize(r.GetString(2)),
        StepOrder = (int)r.GetInt64(3),
        Status = r.GetString(4),
        ExitCode = GetNullableInt(r, 5),
        DurationMs = GetNullableLong(r, 6),
        LogPath = GetNullableString(r, 7),
        FindingsJson = GetNullableString(r, 8),
        Error = GetNullableString(r, 9),
        StartedAt = GetNullableLong(r, 10),
        CompletedAt = GetNullableLong(r, 11),
    };
}
