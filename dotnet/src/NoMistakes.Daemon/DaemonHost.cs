using NoMistakes.Core;
using NoMistakes.Ipc;

namespace NoMistakes.Daemon;

/// <summary>
/// The background daemon: owns the IPC socket, the PID file, and the
/// server-PID tracking directory, all located via <see cref="Paths"/>.
/// Ported from Go internal/daemon daemon.go (RunWithOptions), reduced to the
/// startup/shutdown lifecycle — run management, recovery, and the remaining
/// IPC methods arrive in later slices.
/// </summary>
public sealed class DaemonHost
{
    private readonly Paths paths;
    private readonly IpcServer server = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int shutdownRequested;

    public DaemonHost(Paths paths)
    {
        this.paths = paths;
    }

    /// <summary>
    /// The IPC server; later slices register run-management handlers here
    /// before calling <see cref="RunAsync"/>.
    /// </summary>
    public IpcServer Server => server;

    /// <summary>
    /// Completes when the daemon is accepting connections and its PID file is
    /// written; faults if startup fails.
    /// </summary>
    public Task Ready => ready.Task;

    /// <summary>
    /// Where managed agent servers leave PID breadcrumbs for crash recovery,
    /// from <see cref="Paths.ServerPidsDir"/> (Go points the agent package at
    /// this directory on startup). Created by <see cref="RunAsync"/>.
    /// </summary>
    public string ServerPidsDir => paths.ServerPidsDir;

    /// <summary>
    /// Runs the daemon until <see cref="Shutdown"/> is called or a shutdown
    /// IPC request arrives. On exit the PID file and socket are removed only
    /// if this daemon still owns the PID file — a replacement daemon's
    /// artifacts are never destroyed.
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            paths.EnsureDirs();

            server.Handle(Methods.Health, (_, _) =>
                Task.FromResult<object?>(new HealthResult { Status = "ok" }));
            server.Handle(Methods.Shutdown, (_, _) =>
            {
                // Close in the background so the OK response is written first
                // (Go runs shutdown in a goroutine for the same reason).
                _ = Task.Run(() => Shutdown());
                return Task.FromResult<object?>(new ShutdownResult { Ok = true });
            });

            var pidRecord = DaemonPidRecord.CurrentProcess();
            DaemonPidRecord.Write(paths.PidFile, pidRecord);
            try
            {
                var serve = server.ServeAsync(paths.Socket);
                try
                {
                    await server.Listening.ConfigureAwait(false);
                    ready.TrySetResult();
                }
                catch
                {
                    // ServeAsync carries the actual failure; fall through so
                    // the awaited serve task surfaces it.
                }
                await serve.ConfigureAwait(false);

                // Clean up the socket file only if we still own the PID file.
                // A new daemon may have already replaced socket and PID file.
                if (OwnsPidFile(pidRecord))
                {
                    TryDelete(paths.PidFile);
                    TryDelete(paths.Socket);
                }
            }
            finally
            {
                if (OwnsPidFile(pidRecord))
                {
                    TryDelete(paths.PidFile);
                }
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            throw;
        }
    }

    /// <summary>Requests a graceful shutdown. Idempotent.</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) == 0)
        {
            server.Close();
        }
    }

    private bool OwnsPidFile(DaemonPidRecord pidRecord)
    {
        try
        {
            return DaemonPidRecord.Read(paths.PidFile).SameOwner(pidRecord);
        }
        catch (Exception)
        {
            return false; // missing or unreadable: not ours to delete
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort cleanup, matching Go's ignored os.Remove errors.
        }
    }
}
