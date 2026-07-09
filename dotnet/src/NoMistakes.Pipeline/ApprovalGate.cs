using NoMistakes.Core;
using NoMistakes.Data;

namespace NoMistakes.Pipeline;

/// <summary>
/// The response a driver sends to a step parked at an approval gate. Mirrors
/// Go's pipeline approvalResponse.
/// </summary>
public sealed class ApprovalResponse
{
    public string Action { get; init; } = string.Empty;
    public List<string>? FindingIds { get; init; }
    public Dictionary<string, string>? Instructions { get; init; }
    public List<Finding>? AddedFindings { get; init; }
}

/// <summary>
/// The approval-gate half of Go's pipeline Executor: the waiting/waitingStep
/// state guarded by a mutex, the buffered approval hand-off, and the
/// run-level parked-awaiting-agent marker lifecycle. The slice-9 executor
/// composes this; its RegisterResponder callback delegates to
/// <see cref="RespondWithOverrides"/>.
///
/// Marker invariant (observability only — it never changes gate resolution):
/// runs.awaiting_agent_since is non-null iff a step is actually parked at a
/// gate. <see cref="ParkAsync"/> sets it BEFORE the caller flips the step
/// status to the gate state (so a poller that observes the parked step
/// already sees the marker) and clears it the moment the wait returns,
/// whether the agent responded or the wait was cancelled.
/// </summary>
public sealed class ApprovalGate
{
    private readonly object mu = new();
    private bool waiting;
    private string waitingStep = string.Empty;
    private TaskCompletionSource<ApprovalResponse>? pending;

    /// <summary>
    /// Sends a user approval action to the currently waiting step. The step
    /// must match the step currently awaiting approval. Ports Go
    /// Executor.Respond.
    /// </summary>
    public void Respond(string step, string action, List<string>? findingIds = null) =>
        RespondWithOverrides(step, action, findingIds, null, null);

    /// <summary>
    /// Like <see cref="Respond"/> but also carries per-finding user
    /// instructions and user-authored findings. Throws
    /// InvalidOperationException with Go's exact messages when no step is
    /// waiting or the step name mismatches.
    /// </summary>
    public void RespondWithOverrides(
        string step,
        string action,
        List<string>? findingIds,
        Dictionary<string, string>? instructions,
        List<Finding>? addedFindings)
    {
        TaskCompletionSource<ApprovalResponse> tcs;
        lock (mu)
        {
            if (!waiting)
            {
                throw new InvalidOperationException("no step awaiting approval");
            }
            if (step != waitingStep)
            {
                throw new InvalidOperationException(
                    $"step mismatch: responding to \"{step}\" but \"{waitingStep}\" is awaiting approval");
            }
            waiting = false;
            tcs = pending!;
        }

        tcs.TrySetResult(new ApprovalResponse
        {
            Action = action,
            FindingIds = findingIds,
            Instructions = instructions,
            AddedFindings = addedFindings,
        });
    }

    /// <summary>
    /// Parks the run at an approval gate and blocks until a response arrives
    /// or the token is cancelled (throwing OperationCanceledException).
    /// Ordering ports the Go executor's gate entry: the gate is armed to
    /// receive responses and the awaiting-agent marker is set BEFORE
    /// <paramref name="onParked"/> runs, so the caller flips the step status
    /// to awaiting_approval/fix_review inside that callback and a poller that
    /// sees the parked step can immediately respond and already sees the
    /// marker. The marker is cleared on every exit path.
    /// </summary>
    public async Task<ApprovalResponse> ParkAsync(
        Database db,
        string runId,
        string step,
        Action? onParked,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (mu)
        {
            waiting = true;
            waitingStep = step;
            pending = tcs;
        }
        db.SetRunAwaitingAgent(runId);
        try
        {
            onParked?.Invoke();
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (mu)
            {
                waiting = false;
                waitingStep = string.Empty;
                pending = null;
            }
            db.ClearRunAwaitingAgent(runId);
        }
    }
}
