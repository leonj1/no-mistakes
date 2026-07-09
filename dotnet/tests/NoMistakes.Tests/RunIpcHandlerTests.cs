using System.Net.Sockets;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Run-status and cancel IPC methods served end to end over a unix-domain
/// socket. Ports the get_run/get_runs/get_active_run/cancel_run handlers from
/// Go internal/daemon/daemon.go registerHandlers.
/// </summary>
public class RunIpcHandlerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task GetRunReturnsInfoIncludingAwaitingAgentAndSteps()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        db.InsertStepResult(run.Id, StepName.Review);
        db.SetRunAwaitingAgent(run.Id);
        var since = db.GetRun(run.Id)!.AwaitingAgentSince;

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        var request = Protocol.NewRequest(Methods.GetRun, new GetRunParams { RunId = run.Id });
        await client.WriteAsync(request);
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.Null(response!.Error);

        var result = IpcJson.Deserialize<GetRunResult>(response.Result!.Value)!;
        Assert.NotNull(result.Run);
        Assert.Equal(run.Id, result.Run!.Id);
        Assert.Equal("feature", result.Run.Branch);
        Assert.True(result.Run.AwaitingAgent);
        Assert.Equal(since, result.Run.AwaitingAgentSince);
        Assert.NotNull(result.Run.Steps);
        Assert.Single(result.Run.Steps!);
        Assert.Equal(StepName.Review, result.Run.Steps![0].StepName);
    }

    [Fact]
    public async Task GetRunUnknownIdReturnsError()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(Methods.GetRun, new GetRunParams { RunId = "nope" }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.NotNull(response!.Error);
        Assert.Equal(ErrorCodes.Internal, response.Error!.Code);
        Assert.Equal("run not found: nope", response.Error.Message);
    }

    [Fact]
    public async Task GetRunsReturnsAllRunsForRepo()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var other = db.InsertRepo("/home/user/other", "git@github.com:user/other.git", "main");
        var first = db.InsertRun(repo.Id, "feature", "abc", "def");
        var second = db.InsertRun(repo.Id, "feature-2", "abd", "def");
        db.InsertRun(other.Id, "feature", "abe", "def");

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(Methods.GetRuns, new GetRunsParams { RepoId = repo.Id }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.Null(response!.Error);

        var result = IpcJson.Deserialize<GetRunsResult>(response.Result!.Value)!;
        Assert.Equal(2, result.Runs.Count);
        var ids = result.Runs.Select(r => r.Id).ToHashSet();
        Assert.Contains(first.Id, ids);
        Assert.Contains(second.Id, ids);
    }

    [Fact]
    public async Task GetActiveRunReturnsEmptyResultWhenNoneActive()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");
        db.UpdateRunStatus(run.Id, RunStatus.Completed);

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(
            Methods.GetActiveRun, new GetActiveRunParams { RepoId = repo.Id, Branch = "feature" }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.Null(response!.Error);
        Assert.Null(IpcJson.Deserialize<GetActiveRunResult>(response.Result!.Value)!.Run);
    }

    [Fact]
    public async Task GetActiveRunReturnsPendingRun()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc", "def");

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(
            Methods.GetActiveRun, new GetActiveRunParams { RepoId = repo.Id, Branch = "feature" }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.Null(response!.Error);
        var result = IpcJson.Deserialize<GetActiveRunResult>(response.Result!.Value)!;
        Assert.NotNull(result.Run);
        Assert.Equal(run.Id, result.Run!.Id);
    }

    [Fact]
    public async Task CancelRunOverIpcCancelsActiveRun()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });
        var runId = await manager.StartRunAsync(repo, "feature", "abc", "def").WaitAsync(Timeout);
        await started.Task.WaitAsync(Timeout);
        var done = manager.DoneTask(runId)!;

        await using var host = await ServeAsync(tmp, db, manager);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(Methods.CancelRun, new CancelRunParams { RunId = runId }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.Null(response!.Error);
        Assert.True(IpcJson.Deserialize<CancelRunResult>(response.Result!.Value)!.Ok);

        await done.WaitAsync(Timeout);
        var stored = db.GetRun(runId)!;
        Assert.Equal(RunStatus.Cancelled, stored.Status);
        Assert.Equal(RunCancelReason.AbortedByUser, stored.Error);
    }

    [Fact]
    public async Task CancelRunUnknownIdReturnsError()
    {
        using var tmp = new TempDir();
        using var db = DataTestSupport.OpenTestDb(tmp);

        await using var host = await ServeAsync(tmp, db);
        using var client = await ConnectAsync(host.SocketPath);

        await client.WriteAsync(Protocol.NewRequest(Methods.CancelRun, new CancelRunParams { RunId = "ghost" }));
        var response = await client.ReadAsync<Response>().WaitAsync(Timeout);
        Assert.NotNull(response!.Error);
        Assert.Equal(ErrorCodes.Internal, response.Error!.Code);
        Assert.Equal("no active run ghost", response.Error.Message);
    }

    /// <summary>An IpcServer bound to a socket in the temp dir, torn down on dispose.</summary>
    private sealed class ServerHost : IAsyncDisposable
    {
        public required IpcServer Server { get; init; }
        public required Task Serve { get; init; }
        public required string SocketPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Server.Close();
            await Serve.WaitAsync(Timeout);
        }
    }

    private static async Task<ServerHost> ServeAsync(TempDir tmp, Database db, RunManager? manager = null)
    {
        manager ??= new RunManager(db, (_, _, _) => Task.CompletedTask);
        var server = new IpcServer();
        RunIpcHandlers.Register(server, manager, db);
        var socketPath = tmp.File("run.sock");
        var serve = Task.Run(() => server.ServeAsync(socketPath));
        await server.Listening.WaitAsync(Timeout);
        return new ServerHost { Server = server, Serve = serve, SocketPath = socketPath };
    }

    private static async Task<JsonLineStream> ConnectAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return new JsonLineStream(new NetworkStream(socket, ownsSocket: true));
    }
}
