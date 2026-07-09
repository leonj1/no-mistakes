using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Ipc;

namespace NoMistakes.Cli;

/// <summary>
/// Result of an abort-by-id request. <see cref="Detail"/> is non-null exactly
/// when the abort was a no-op, carrying the Go CLI's detail line for the TOON
/// render (slice 8 wires the command surface and rendering).
/// </summary>
public sealed record AxiAbortOutcome(bool Aborted, string RunId, string? Detail);

/// <summary>
/// Cancels a run by its id directly via the daemon, without resolving a repo,
/// branch, or worktree — it needs only NM_HOME (via <see cref="Paths"/>) plus
/// the daemon. This is how an orphaned monitor run — one whose worktree was
/// torn down before the PR merged — gets reaped from outside. A run lives only
/// in the running daemon's memory, so if the daemon is not running, or the id
/// is not an active run, there is nothing to cancel and we report a successful
/// no-op (the desired end state is already reached). Ported from Go
/// internal/cli/axi_drive.go runAxiAbortByRunID.
/// </summary>
public static class AxiAbort
{
    public static async Task<AxiAbortOutcome> AbortByRunIdAsync(Paths paths, string runId, CancellationToken ct = default)
    {
        paths.EnsureDirs();

        if (!await DaemonStatus.IsRunningAsync(paths, ct).ConfigureAwait(false))
        {
            return new AxiAbortOutcome(false, runId, "daemon not running, so no active run to cancel (no-op)");
        }

        using var client = await IpcClient.DialAsync(paths.Socket, ct).ConfigureAwait(false);
        try
        {
            await client.CallAsync<CancelRunResult>(Methods.CancelRun, new CancelRunParams { RunId = runId }, ct)
                .ConfigureAwait(false);
        }
        catch (IpcRpcException ex) when (ex.Message.Contains("no active run"))
        {
            // The daemon reports an unknown/inactive run id as "no active run
            // <id>". Treat that as an idempotent no-op: the run is already gone.
            return new AxiAbortOutcome(false, runId, "no active run with that id (no-op)");
        }
        return new AxiAbortOutcome(true, runId, null);
    }
}
