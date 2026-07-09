using System.Text.Json.Serialization;

namespace NoMistakes.Ipc;

/// <summary>The IPC representation of a pipeline run. Mirrors Go ipc.RunInfo.</summary>
public sealed class RunInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("head_sha")]
    public string HeadSha { get; set; } = string.Empty;

    [JsonPropertyName("base_sha")]
    public string BaseSha { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// True while the run is parked at a gate awaiting the driving agent's
    /// response; AwaitingAgentSince is the unix-seconds time it parked. Both
    /// are observability only and clear the moment the agent responds.
    /// </summary>
    [JsonPropertyName("awaiting_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AwaitingAgent { get; set; }

    [JsonPropertyName("awaiting_agent_since")]
    public long? AwaitingAgentSince { get; set; }

    [JsonPropertyName("steps")]
    public List<StepResultInfo>? Steps { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; set; }
}

/// <summary>The IPC representation of a step result. Mirrors Go ipc.StepResultInfo.</summary>
public sealed class StepResultInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("step_name")]
    [JsonConverter(typeof(StepNameJsonConverter))]
    public string StepName { get; set; } = string.Empty;

    [JsonPropertyName("step_order")]
    public int StepOrder { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("findings_json")]
    public string? FindingsJson { get; set; }

    [JsonPropertyName("reported_findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReportedFindings { get; set; }

    [JsonPropertyName("fixed_findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FixedFindings { get; set; }

    /// <summary>
    /// One entry per fix round the pipeline ran for this step, in round order:
    /// the agent's one-line fix summary, or "" when the round recorded none.
    /// </summary>
    [JsonPropertyName("fix_summaries")]
    public List<string>? FixSummaries { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("started_at")]
    public long? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public long? CompletedAt { get; set; }
}

/// <summary>Event kinds for the subscribe stream. Mirrors Go ipc.EventType.</summary>
public static class EventTypes
{
    public const string RunCreated = "run_created";
    public const string RunUpdated = "run_updated";
    public const string RunCompleted = "run_completed";
    public const string StepStarted = "step_started";
    public const string StepCompleted = "step_completed";
    public const string LogChunk = "log_chunk";
}

/// <summary>A real-time update sent to subscribers. Mirrors Go ipc.Event.</summary>
public sealed class IpcEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("step_name")]
    [JsonConverter(typeof(StepNameJsonConverter))]
    public string? StepName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("stream")]
    public string? Stream { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>JSON-encoded findings for step_completed events.</summary>
    [JsonPropertyName("findings")]
    public string? Findings { get; set; }

    /// <summary>Unified diff for fix_review events.</summary>
    [JsonPropertyName("diff")]
    public string? Diff { get; set; }

    [JsonPropertyName("reported_findings")]
    public int? ReportedFindings { get; set; }

    [JsonPropertyName("fixed_findings")]
    public int? FixedFindings { get; set; }

    /// <summary>Execution-only duration for step events.</summary>
    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; set; }

    /// <summary>PR URL for run_updated/run_completed events.</summary>
    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; set; }
}
