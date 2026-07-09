using NoMistakes.Core;
using NoMistakes.Ipc;

namespace NoMistakes.Daemon;

/// <summary>
/// Client-side daemon liveness probe. Ported from Go internal/daemon
/// selfexec.go IsRunning/daemonIsRunningViaIPC: alive means the socket accepts
/// a connection and the health method answers "ok". Any dial or call failure
/// reads as not running (fail-safe: callers treat "not running" as
/// nothing-to-do, never as permission to skip cleanup).
/// </summary>
public static class DaemonStatus
{
    public static async Task<bool> IsRunningAsync(Paths paths, CancellationToken ct = default)
    {
        IpcClient client;
        try
        {
            client = await IpcClient.DialAsync(paths.Socket, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        using (client)
        {
            try
            {
                var result = await client.CallAsync<HealthResult>(Methods.Health, new HealthParams(), ct)
                    .ConfigureAwait(false);
                return result?.Status == "ok";
            }
            catch (Exception ex) when (ex is IOException or IpcRpcException)
            {
                return false;
            }
        }
    }
}
