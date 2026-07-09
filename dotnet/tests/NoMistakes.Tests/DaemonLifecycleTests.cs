using System.Net.Sockets;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Daemon startup/shutdown lifecycle: socket creation, PID-file ownership,
/// and the health/shutdown IPC methods. Ported from Go internal/daemon's
/// RunWithOptions behavior (daemon.go).
/// </summary>
[Collection("daemon")]
public class DaemonLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartServesHealthOverSocketAndWritesPidFile()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);

        var record = DaemonPidRecord.Read(paths.PidFile);
        Assert.Equal(Environment.ProcessId, record.Pid);
        Assert.NotNull(record.StartedAt);
        // Server-PID tracking dir comes from Paths and exists after startup.
        Assert.Equal(paths.ServerPidsDir, daemon.ServerPidsDir);
        Assert.True(Directory.Exists(daemon.ServerPidsDir));

        using (var client = await ConnectAsync(paths.Socket))
        {
            var request = Protocol.NewRequest(Methods.Health, new HealthParams());
            await client.WriteAsync(request);
            var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
            Assert.Null(response!.Error);
            Assert.Equal(request.Id, response.Id);
            Assert.Equal("ok", IpcJson.Deserialize<HealthResult>(response.Result!.Value)!.Status);
        }

        daemon.Shutdown();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ShutdownIpcRequestStopsDaemonAndRemovesArtifacts()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);

        using (var client = await ConnectAsync(paths.Socket))
        {
            var request = Protocol.NewRequest(Methods.Shutdown, new ShutdownParams());
            await client.WriteAsync(request);
            var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
            Assert.Null(response!.Error);
            Assert.True(IpcJson.Deserialize<ShutdownResult>(response.Result!.Value)!.Ok);
        }

        await run.WaitAsync(Timeout);
        Assert.False(File.Exists(paths.PidFile));
        Assert.False(File.Exists(paths.Socket));
    }

    [Fact]
    public async Task ShutdownMethodStopsDaemonAndRemovesArtifacts()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);
        Assert.True(File.Exists(paths.PidFile));

        daemon.Shutdown();
        daemon.Shutdown(); // idempotent
        await run.WaitAsync(Timeout);

        Assert.False(File.Exists(paths.PidFile));
        Assert.False(File.Exists(paths.Socket));
    }

    [Fact]
    public async Task StaleSocketFileIsReplacedOnStartup()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        paths.EnsureDirs();
        // A crashed predecessor left a dead socket file behind.
        File.WriteAllText(paths.Socket, "stale");

        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);

        using (var client = await ConnectAsync(paths.Socket))
        {
            await client.WriteAsync(Protocol.NewRequest(Methods.Health, new HealthParams()));
            var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
            Assert.Equal("ok", IpcJson.Deserialize<HealthResult>(response!.Result!.Value)!.Status);
        }

        daemon.Shutdown();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task StoppingDaemonKeepsPidFileOwnedByReplacementDaemon()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);

        // A newer daemon replaced the PID file while we were running.
        var replacement = new DaemonPidRecord
        {
            Pid = Environment.ProcessId + 1,
            StartedAt = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero),
        };
        DaemonPidRecord.Write(paths.PidFile, replacement);

        daemon.Shutdown();
        await run.WaitAsync(Timeout);

        // The old daemon must not destroy the replacement's PID file. (The
        // socket file itself is unlinked when the listener closes — same as
        // Go's net.UnixListener unlink-on-close — so only the PID file
        // ownership is asserted here.)
        var kept = DaemonPidRecord.Read(paths.PidFile);
        Assert.True(kept.SameOwner(replacement));
    }

    [Fact]
    public async Task UnknownMethodGetsMethodNotFoundError()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);

        using (var client = await ConnectAsync(paths.Socket))
        {
            var request = Protocol.NewRequest("bogus_method", new HealthParams());
            await client.WriteAsync(request);
            var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
            Assert.NotNull(response!.Error);
            Assert.Equal(ErrorCodes.MethodNotFound, response.Error!.Code);
        }

        daemon.Shutdown();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task StartupFailureFaultsReadyAndRunTask()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        paths.EnsureDirs();
        // Occupy the socket path with a directory so the listener bind fails.
        Directory.CreateDirectory(paths.Socket);

        var daemon = new DaemonHost(paths);
        var run = Task.Run(daemon.RunAsync);
        await Assert.ThrowsAnyAsync<Exception>(() => daemon.Ready.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Timeout));
        // A failed startup must not leave a PID file claiming a live daemon.
        Assert.False(File.Exists(paths.PidFile));
    }

    private static async Task<JsonLineStream> ConnectAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return new JsonLineStream(new NetworkStream(socket, ownsSocket: true));
    }
}
