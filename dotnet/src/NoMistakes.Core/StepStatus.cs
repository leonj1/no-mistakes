namespace NoMistakes.Core;

/// <summary>
/// Lifecycle states of a pipeline step. Mirrors Go's types.StepStatus string
/// constants; the values are the exact strings persisted in step_results.
/// </summary>
public static class StepStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Fixing = "fixing";
    public const string FixReview = "fix_review";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
