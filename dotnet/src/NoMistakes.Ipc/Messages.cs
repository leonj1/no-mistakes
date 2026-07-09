using System.Text.Json.Serialization;
using NoMistakes.Core;

namespace NoMistakes.Ipc;

// Method parameter and result types. Mirrors Go internal/ipc protocol.go.
// Step names carry StepNameJsonConverter so the retired "babysit" name is
// normalized to "ci" on read, like Go's types.StepName.UnmarshalJSON.

/// <summary>
/// Sent by the post-receive hook (`daemon notify-push`) when a push arrives.
/// Intent, when set, is an agent-supplied description of the change; it is
/// stamped onto the run so the intent step uses it verbatim.
/// </summary>
public sealed class PushReceivedParams
{
    [JsonPropertyName("gate")]
    public string Gate { get; set; } = string.Empty;

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("old")]
    public string Old { get; set; } = string.Empty;

    [JsonPropertyName("new")]
    public string New { get; set; } = string.Empty;

    [JsonPropertyName("skip_steps")]
    [JsonConverter(typeof(StepNameListJsonConverter))]
    public List<string>? SkipSteps { get; set; }

    [JsonPropertyName("intent")]
    public string? Intent { get; set; }
}

/// <summary>Requests a single run by ID.</summary>
public sealed class GetRunParams
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>Requests all runs for a repo.</summary>
public sealed class GetRunsParams
{
    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;
}

/// <summary>
/// Requests the active run for a repo. When Branch is set, runs on that
/// branch are preferred.
/// </summary>
public sealed class GetActiveRunParams
{
    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}

/// <summary>
/// Requests a new run for the latest gate head on a branch. Intent, when set,
/// is stamped onto the new run like <see cref="PushReceivedParams.Intent"/>.
/// </summary>
public sealed class RerunParams
{
    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("skip_steps")]
    [JsonConverter(typeof(StepNameListJsonConverter))]
    public List<string>? SkipSteps { get; set; }

    [JsonPropertyName("intent")]
    public string? Intent { get; set; }
}

/// <summary>Starts an event stream for a run.</summary>
public sealed class SubscribeParams
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>
/// Sends a user action for a step awaiting approval. Instructions carries
/// optional per-finding notes keyed by finding ID; AddedFindings carries
/// user-authored findings merged into the round alongside agent-produced
/// ones. Both only apply when Action triggers a fix round.
/// </summary>
public sealed class RespondParams
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("step")]
    [JsonConverter(typeof(StepNameJsonConverter))]
    public string Step { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("finding_ids")]
    public List<string>? FindingIds { get; set; }

    [JsonPropertyName("instructions")]
    public Dictionary<string, string>? Instructions { get; set; }

    [JsonPropertyName("added_findings")]
    public List<Finding>? AddedFindings { get; set; }
}

/// <summary>Cancels an active pipeline run.</summary>
public sealed class CancelRunParams
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>No fields; exists for consistency.</summary>
public sealed class HealthParams
{
}

/// <summary>No fields; exists for consistency.</summary>
public sealed class ShutdownParams
{
}

/// <summary>Confirms the push was accepted.</summary>
public sealed class PushReceivedResult
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>Wraps a single run.</summary>
public sealed class GetRunResult
{
    [JsonPropertyName("run")]
    public RunInfo? Run { get; set; }
}

/// <summary>Wraps a list of runs.</summary>
public sealed class GetRunsResult
{
    [JsonPropertyName("runs")]
    public List<RunInfo> Runs { get; set; } = new();
}

/// <summary>Wraps the active run (null if none).</summary>
public sealed class GetActiveRunResult
{
    [JsonPropertyName("run")]
    public RunInfo? Run { get; set; }
}

/// <summary>Confirms a rerun was created.</summary>
public sealed class RerunResult
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>Confirms the action was accepted.</summary>
public sealed class RespondResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}

/// <summary>Confirms the run cancellation request was accepted.</summary>
public sealed class CancelRunResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}

/// <summary>Confirms the daemon is alive.</summary>
public sealed class HealthResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Confirms shutdown was initiated.</summary>
public sealed class ShutdownResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}
