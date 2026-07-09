namespace NoMistakes.Core;

/// <summary>
/// Lifecycle states of a pipeline run. Mirrors Go's types.RunStatus string
/// constants; the values are the exact strings persisted in the runs table.
/// </summary>
public static class RunStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
