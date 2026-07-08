using Microsoft.Data.Sqlite;

namespace NoMistakes.Data;

/// <summary>One execution round within a pipeline step. Mirrors Go's db.StepRound.</summary>
public sealed class StepRound
{
    public string Id { get; set; } = string.Empty;
    public string StepResultId { get; set; } = string.Empty;
    public int Round { get; set; }

    /// <summary>"initial" or "auto_fix"; legacy "user_fix" is treated as "auto_fix".</summary>
    public string Trigger { get; set; } = string.Empty;

    public string? FindingsJson { get; set; }

    /// <summary>
    /// When non-null, the merged finding list dispatched to the fix agent after
    /// the user edited per-finding instructions or added their own findings.
    /// </summary>
    public string? UserFindingsJson { get; set; }

    /// <summary>
    /// When non-null, a JSON array of finding IDs chosen (by the user or the
    /// auto-fix filter) to be fixed after this round. Recorded on the round whose
    /// findings triggered the next round.
    /// </summary>
    public string? SelectedFindingIds { get; set; }

    public string? SelectionSource { get; set; }

    /// <summary>The agent's one-line commit summary, only set on fix rounds.</summary>
    public string? FixSummary { get; set; }

    public long DurationMs { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>
    /// Reports whether this round was a fix attempt. Legacy "user_fix" rounds
    /// count: they were fix rounds dispatched by an explicit user selection.
    /// </summary>
    public bool IsFixRound() => Trigger is "auto_fix" or "user_fix";
}

public sealed partial class Database
{
    /// <summary>Selection-source constant: the selection came from the user.</summary>
    public const string RoundSelectionSourceUser = "user";

    /// <summary>Selection-source constant: the selection came from the auto-fix filter.</summary>
    public const string RoundSelectionSourceAutoFix = "auto_fix";

    private const string RoundSelectColumns =
        "id, step_result_id, round, trigger_type, findings_json, user_findings_json, selected_finding_ids, selection_source, fix_summary, duration_ms, created_at";

    /// <summary>Creates a new round record for a step result.</summary>
    public StepRound InsertStepRound(string stepResultId, int round, string trigger, string? findingsJson, string? fixSummary, long durationMs)
    {
        var r = new StepRound
        {
            Id = NewId(),
            StepResultId = stepResultId,
            Round = round,
            Trigger = trigger,
            FindingsJson = findingsJson,
            FixSummary = fixSummary,
            DurationMs = durationMs,
            CreatedAt = Now(),
        };
        lock (gate)
        {
            using var cmd = NewCommand(
                "INSERT INTO step_rounds (id, step_result_id, round, trigger_type, findings_json, user_findings_json, selected_finding_ids, selection_source, fix_summary, duration_ms, created_at) VALUES ($id, $step, $round, $trigger, $findings, $userFindings, $selected, $source, $fix, $dur, $created)");
            Bind(cmd, "$id", r.Id);
            Bind(cmd, "$step", r.StepResultId);
            Bind(cmd, "$round", r.Round);
            Bind(cmd, "$trigger", r.Trigger);
            Bind(cmd, "$findings", r.FindingsJson);
            Bind(cmd, "$userFindings", r.UserFindingsJson);
            Bind(cmd, "$selected", r.SelectedFindingIds);
            Bind(cmd, "$source", r.SelectionSource);
            Bind(cmd, "$fix", r.FixSummary);
            Bind(cmd, "$dur", r.DurationMs);
            Bind(cmd, "$created", r.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        return r;
    }

    /// <summary>
    /// Records which findings were selected for fix after the given round, along
    /// with whether that selection came from the user or the auto-fix filter.
    /// Passing null or an empty JSON array clears both columns.
    /// </summary>
    public void SetStepRoundSelection(string id, string? selectedFindingIds, string source)
    {
        string? selectionSource = null;
        if (!string.IsNullOrEmpty(selectedFindingIds) && !string.IsNullOrEmpty(source))
        {
            selectionSource = source;
        }
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_rounds SET selected_finding_ids = $selected, selection_source = $source WHERE id = $id");
            Bind(cmd, "$selected", selectedFindingIds);
            Bind(cmd, "$source", selectionSource);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Preserves the older API for callers that do not need to distinguish how the
    /// selection was made.
    /// </summary>
    public void SetStepRoundSelectedFindingIds(string id, string? selectedFindingIds) =>
        SetStepRoundSelection(id, selectedFindingIds, RoundSelectionSourceUser);

    /// <summary>
    /// Records the merged finding list (with user instructions attached and
    /// user-added findings appended) dispatched to the fix agent. Passing null
    /// clears the column.
    /// </summary>
    public void SetStepRoundUserFindings(string id, string? userFindingsJson)
    {
        lock (gate)
        {
            using var cmd = NewCommand("UPDATE step_rounds SET user_findings_json = $findings WHERE id = $id");
            Bind(cmd, "$findings", userFindingsJson);
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Returns all rounds for a step result, ordered by round number.</summary>
    public List<StepRound> GetRoundsByStep(string stepResultId)
    {
        lock (gate)
        {
            using var cmd = NewCommand($"SELECT {RoundSelectColumns} FROM step_rounds WHERE step_result_id = $step ORDER BY round");
            Bind(cmd, "$step", stepResultId);
            using var r = cmd.ExecuteReader();
            var rounds = new List<StepRound>();
            while (r.Read())
            {
                rounds.Add(ScanRound(r));
            }
            return rounds;
        }
    }

    /// <summary>
    /// Returns one entry per fix round for a step, in round order: the agent's
    /// one-line fix summary, or "" when the round recorded none.
    /// </summary>
    public List<string> StepFixSummaries(string stepResultId)
    {
        var summaries = new List<string>();
        foreach (var round in GetRoundsByStep(stepResultId))
        {
            if (!round.IsFixRound())
            {
                continue;
            }
            summaries.Add(round.FixSummary ?? string.Empty);
        }
        return summaries;
    }

    private static StepRound ScanRound(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        StepResultId = r.GetString(1),
        Round = (int)r.GetInt64(2),
        Trigger = r.GetString(3),
        FindingsJson = GetNullableString(r, 4),
        UserFindingsJson = GetNullableString(r, 5),
        SelectedFindingIds = GetNullableString(r, 6),
        SelectionSource = GetNullableString(r, 7),
        FixSummary = GetNullableString(r, 8),
        DurationMs = r.GetInt64(9),
        CreatedAt = r.GetInt64(10),
    };
}
