namespace NoMistakes.Core;

/// <summary>
/// Cancellation causes recorded on a run when its pipeline is stopped early.
/// Mirrors Go's types.RunCancelReason* constants plus the shutdown cause the
/// daemon passes when cancelling runs on exit; the strings become the run's
/// error message, so they must match Go byte for byte.
/// </summary>
public static class RunCancelReason
{
    public const string AbortedByUser = "cancelled: aborted by user";
    public const string Superseded = "cancelled: superseded by new push";
    public const string DaemonShutdown = "daemon shutting down";
}
