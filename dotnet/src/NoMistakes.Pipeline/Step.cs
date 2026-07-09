using NoMistakes.Config;
using NoMistakes.Data;

namespace NoMistakes.Pipeline;

/// <summary>
/// Shared resources handed to a pipeline step during execution. Mirrors Go's
/// pipeline.StepContext. The native-agent handle lands with slice 14; until then
/// <see cref="Agent"/> is an opaque nullable placeholder so step ports can carry
/// it without depending on the agent layer.
/// </summary>
public sealed class StepContext
{
    public required CancellationToken Ct { get; init; }
    public required Run Run { get; init; }
    public required Repo Repo { get; init; }
    public required string WorkDir { get; init; }
    public object? Agent { get; init; }
    public Config.Config? Config { get; init; }
    public required Database Db { get; init; }

    /// <summary>Discrete log line (newline-terminated), user-visible and to file.</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Raw streaming chunk, user-visible and to file.</summary>
    public Action<string> LogChunk { get; init; } = _ => { };

    /// <summary>File-only log callback (not shown to the user).</summary>
    public Action<string> LogFile { get; init; } = _ => { };

    /// <summary>True when re-executing after a "fix" action.</summary>
    public bool Fixing { get; set; }

    /// <summary>JSON findings from the previous execution (set during the fix loop).</summary>
    public string PreviousFindings { get; set; } = string.Empty;

    /// <summary>DB row ID of the current step's step_results record.</summary>
    public required string StepResultId { get; init; }

    /// <summary>Short, possibly-empty summary of what the change author intended.</summary>
    public string UserIntent { get; init; } = string.Empty;
}

/// <summary>The result of executing a pipeline step. Mirrors Go's pipeline.StepOutcome.</summary>
public sealed class StepOutcome
{
    /// <summary>Whether the step pauses for user action.</summary>
    public bool NeedsApproval { get; init; }

    public bool AutoFixable { get; init; }

    /// <summary>JSON findings for display (optional).</summary>
    public string Findings { get; init; } = string.Empty;

    /// <summary>Process exit code (0 = success).</summary>
    public int ExitCode { get; init; }

    /// <summary>PR/MR URL if this step created or found one.</summary>
    public string PrUrl { get; init; } = string.Empty;

    /// <summary>Mark the step skipped without failing the run.</summary>
    public bool Skipped { get; init; }

    /// <summary>Skip all subsequent steps (e.g. empty diff after rebase).</summary>
    public bool SkipRemaining { get; init; }

    /// <summary>The agent's one-line commit summary for a fix attempt (fix rounds only).</summary>
    public string FixSummary { get; init; } = string.Empty;

    /// <summary>When positive, replaces the reported wall-clock duration (demo mode).</summary>
    public long DurationOverrideMs { get; init; }
}

/// <summary>The interface each pipeline step implements. Mirrors Go's pipeline.Step.</summary>
public interface IStep
{
    /// <summary>The step's identity in the fixed pipeline sequence.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the step logic. A step returning NeedsApproval=true pauses the
    /// pipeline until the driver responds with an approval action.
    /// </summary>
    Task<StepOutcome> ExecuteAsync(StepContext sctx);
}
