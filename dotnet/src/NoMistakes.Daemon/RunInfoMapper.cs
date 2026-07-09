using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Ipc;

namespace NoMistakes.Daemon;

/// <summary>
/// Maps database rows to their IPC wire shapes. Ported from Go
/// internal/daemon/daemon.go runToInfo/stepToInfo.
/// </summary>
public static class RunInfoMapper
{
    /// <summary>
    /// Converts a run row plus its step rows into the wire RunInfo. The
    /// awaiting-agent fields derive from awaiting_agent_since: AwaitingAgent
    /// is true exactly while the marker is set (run parked at a gate awaiting
    /// the driving agent). Steps stays null when there are none, matching
    /// Go's omitempty.
    /// </summary>
    public static RunInfo RunToInfo(Database db, Run run, IReadOnlyList<StepResult> steps)
    {
        var info = new RunInfo
        {
            Id = run.Id,
            RepoId = run.RepoId,
            Branch = run.Branch,
            HeadSha = run.HeadSha,
            BaseSha = run.BaseSha,
            Status = run.Status,
            PrUrl = run.PrUrl,
            Error = run.Error,
            AwaitingAgent = run.AwaitingAgentSince != null,
            AwaitingAgentSince = run.AwaitingAgentSince,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };
        if (steps.Count > 0)
        {
            info.Steps = new List<StepResultInfo>(steps.Count);
            foreach (var step in steps)
            {
                info.Steps.Add(StepToInfo(db, step));
            }
        }
        return info;
    }

    /// <summary>
    /// Converts a step row into the wire StepResultInfo, enriching it with
    /// finding stats and fix summaries. Both enrichments are best-effort like
    /// Go's ignored errors; FixSummaries stays null when there were no fix
    /// rounds, matching Go's omitempty nil slice.
    /// </summary>
    public static StepResultInfo StepToInfo(Database db, StepResult step)
    {
        var info = new StepResultInfo
        {
            Id = step.Id,
            RunId = step.RunId,
            StepName = step.StepName,
            StepOrder = step.StepOrder,
            Status = step.Status,
            ExitCode = step.ExitCode,
            DurationMs = step.DurationMs,
            FindingsJson = step.FindingsJson,
            Error = step.Error,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
        };
        try
        {
            var stats = db.StepFindingStats(step);
            info.ReportedFindings = stats.ReportedFindings;
            info.FixedFindings = stats.FixedFindings;
        }
        catch (Exception)
        {
            // Stats are decorative; a read failure leaves the zero counts.
        }
        try
        {
            var summaries = db.StepFixSummaries(step.Id);
            if (summaries.Count > 0)
            {
                info.FixSummaries = summaries;
            }
        }
        catch (Exception)
        {
            // Same best-effort contract as the stats read.
        }
        return info;
    }
}
