using System.Net.Sockets;
using System.Text;
using NoMistakes.Core;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Serialization round-trips of the IPC protocol over a real socket pair,
/// using the newline-delimited JSON framing the daemon connection speaks.
/// </summary>
public class IpcSocketRoundTripTests
{
    [Fact]
    public async Task CancelRunRequestResponseRoundTrip()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            var request = Protocol.NewRequest(Methods.CancelRun, new CancelRunParams { RunId = "01RUN" });
            await client.WriteAsync(request);

            var received = await server.ReadAsync<Request>();
            Assert.NotNull(received);
            Assert.Equal("2.0", received!.Jsonrpc);
            Assert.Equal(Methods.CancelRun, received.Method);
            var parameters = IpcJson.Deserialize<CancelRunParams>(received.Params!.Value)!;
            Assert.Equal("01RUN", parameters.RunId);

            await server.WriteAsync(Protocol.NewResponse(received.Id, new CancelRunResult { Ok = true }));

            var response = await client.ReadAsync<Response>();
            Assert.NotNull(response);
            Assert.Equal(request.Id, response!.Id);
            Assert.Null(response.Error);
            var result = IpcJson.Deserialize<CancelRunResult>(response.Result!.Value)!;
            Assert.True(result.Ok);
        }
    }

    [Fact]
    public async Task NotifyPushRequestResponseRoundTrip()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            // The push_received message the post-receive hook's
            // `daemon notify-push` command sends to the daemon.
            var parameters = new PushReceivedParams
            {
                Gate = "/home/user/.no-mistakes/repos/abc123.git",
                Ref = "refs/heads/feature",
                Old = "0000000000000000000000000000000000000000",
                New = "abc123def456",
                SkipSteps = new List<string> { StepName.Review },
                Intent = "port the ipc protocol",
            };
            await client.WriteAsync(Protocol.NewRequest(Methods.PushReceived, parameters));

            var received = await server.ReadAsync<Request>();
            Assert.Equal(Methods.PushReceived, received!.Method);
            var got = IpcJson.Deserialize<PushReceivedParams>(received.Params!.Value)!;
            Assert.Equal(parameters.Gate, got.Gate);
            Assert.Equal(parameters.Ref, got.Ref);
            Assert.Equal(parameters.Old, got.Old);
            Assert.Equal(parameters.New, got.New);
            Assert.Equal(parameters.SkipSteps, got.SkipSteps);
            Assert.Equal(parameters.Intent, got.Intent);

            await server.WriteAsync(Protocol.NewResponse(received.Id, new PushReceivedResult { RunId = "01NEWRUN" }));

            var response = await client.ReadAsync<Response>();
            var result = IpcJson.Deserialize<PushReceivedResult>(response!.Result!.Value)!;
            Assert.Equal("01NEWRUN", result.RunId);
        }
    }

    [Fact]
    public async Task ErrorResponseRoundTrip()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            await client.WriteAsync(Protocol.NewRequest("bogus_method", new HealthParams()));

            var received = await server.ReadAsync<Request>();
            await server.WriteAsync(Protocol.NewErrorResponse(
                received!.Id, ErrorCodes.MethodNotFound, "method not found"));

            var response = await client.ReadAsync<Response>();
            Assert.Null(response!.Result);
            Assert.NotNull(response.Error);
            Assert.Equal(ErrorCodes.MethodNotFound, response.Error!.Code);
            Assert.Equal("method not found", response.Error.Message);
        }
    }

    [Fact]
    public async Task MultipleMessagesOnOneConnectionStayFramed()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            var first = Protocol.NewRequest(Methods.GetRun, new GetRunParams { RunId = "r1" });
            var second = Protocol.NewRequest(Methods.GetRuns, new GetRunsParams { RepoId = "repo1" });
            await client.WriteAsync(first);
            await client.WriteAsync(second);

            var gotFirst = await server.ReadAsync<Request>();
            var gotSecond = await server.ReadAsync<Request>();
            Assert.Equal(Methods.GetRun, gotFirst!.Method);
            Assert.Equal(first.Id, gotFirst.Id);
            Assert.Equal(Methods.GetRuns, gotSecond!.Method);
            Assert.Equal(second.Id, gotSecond.Id);
            Assert.Equal("r1", IpcJson.Deserialize<GetRunParams>(gotFirst.Params!.Value)!.RunId);
            Assert.Equal("repo1", IpcJson.Deserialize<GetRunsParams>(gotSecond.Params!.Value)!.RepoId);
        }
    }

    [Fact]
    public async Task SubscribeResponseThenEventStream()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            var request = Protocol.NewRequest(Methods.Subscribe, new SubscribeParams { RunId = "run001" });
            await client.WriteAsync(request);

            var received = await server.ReadAsync<Request>();
            await server.WriteAsync(Protocol.NewResponse(received!.Id, new { }));
            await server.WriteAsync(new IpcEvent
            {
                Type = EventTypes.StepStarted,
                RunId = "run001",
                RepoId = "repo001",
                StepName = StepName.Review,
            });
            await server.WriteAsync(new IpcEvent
            {
                Type = EventTypes.RunCompleted,
                RunId = "run001",
                RepoId = "repo001",
                Status = RunStatus.Completed,
                PrUrl = "https://github.com/o/r/pull/7",
            });

            var response = await client.ReadAsync<Response>();
            Assert.Equal(request.Id, response!.Id);

            var started = await client.ReadAsync<IpcEvent>();
            Assert.Equal(EventTypes.StepStarted, started!.Type);
            Assert.Equal(StepName.Review, started.StepName);

            var completed = await client.ReadAsync<IpcEvent>();
            Assert.Equal(EventTypes.RunCompleted, completed!.Type);
            Assert.Equal(RunStatus.Completed, completed.Status);
            Assert.Equal("https://github.com/o/r/pull/7", completed.PrUrl);
        }
    }

    [Fact]
    public async Task RunInfoWithStepsRoundTrip()
    {
        var (client, server) = SocketPair();
        using (client)
        using (server)
        {
            var run = new RunInfo
            {
                Id = "run001",
                RepoId = "repo001",
                Branch = "feature",
                HeadSha = "abc",
                BaseSha = "def",
                Status = RunStatus.Running,
                AwaitingAgent = true,
                AwaitingAgentSince = 1700000123,
                Steps = new List<StepResultInfo>
                {
                    new()
                    {
                        Id = "s1",
                        RunId = "run001",
                        StepName = StepName.Review,
                        StepOrder = 3,
                        Status = StepStatus.AwaitingApproval,
                        ReportedFindings = 2,
                    },
                },
                CreatedAt = 1700000000,
                UpdatedAt = 1700000100,
            };
            await server.WriteAsync(Protocol.NewResponse(9, new GetRunResult { Run = run }));

            var response = await client.ReadAsync<Response>();
            var got = IpcJson.Deserialize<GetRunResult>(response!.Result!.Value)!.Run!;
            Assert.Equal("run001", got.Id);
            Assert.True(got.AwaitingAgent);
            Assert.Equal(1700000123L, got.AwaitingAgentSince);
            var step = Assert.Single(got.Steps!);
            Assert.Equal(StepName.Review, step.StepName);
            Assert.Equal(StepStatus.AwaitingApproval, step.Status);
            Assert.Equal(2, step.ReportedFindings);
        }
    }

    [Fact]
    public async Task OversizedMessageIsRejected()
    {
        var (clientSocket, serverSocket) = RawSocketPair();
        using (clientSocket)
        using (serverSocket)
        using (var clientStream = new NetworkStream(clientSocket, ownsSocket: false))
        using (var server = new JsonLineStream(new NetworkStream(serverSocket, ownsSocket: false)))
        {
            var writer = Task.Run(async () =>
            {
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'a');
                var written = 0;
                while (written <= JsonLineStream.MaxMessageBytes)
                {
                    await clientStream.WriteAsync(chunk);
                    written += chunk.Length;
                }
                await clientStream.FlushAsync();
            });

            await Assert.ThrowsAsync<IOException>(() => server.ReadAsync<Request>());
            await writer;
        }
    }

    [Fact]
    public async Task ConnectionClosedMidMessageThrows()
    {
        var (clientSocket, serverSocket) = RawSocketPair();
        using (serverSocket)
        using (var server = new JsonLineStream(new NetworkStream(serverSocket, ownsSocket: false)))
        {
            using (clientSocket)
            using (var clientStream = new NetworkStream(clientSocket, ownsSocket: false))
            {
                await clientStream.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\""));
                await clientStream.FlushAsync();
                clientSocket.Shutdown(SocketShutdown.Send);
            }

            await Assert.ThrowsAsync<IOException>(() => server.ReadAsync<Request>());
        }
    }

    [Fact]
    public async Task CleanCloseAtMessageBoundaryReturnsNull()
    {
        var (clientSocket, serverSocket) = RawSocketPair();
        using (serverSocket)
        using (var server = new JsonLineStream(new NetworkStream(serverSocket, ownsSocket: false)))
        {
            using (clientSocket)
            using (var client = new JsonLineStream(new NetworkStream(clientSocket, ownsSocket: false)))
            {
                await client.WriteAsync(Protocol.NewRequest(Methods.Health, new HealthParams()));
                clientSocket.Shutdown(SocketShutdown.Send);
            }

            var request = await server.ReadAsync<Request>();
            Assert.Equal(Methods.Health, request!.Method);
            Assert.Null(await server.ReadAsync<Request>());
        }
    }

    private static (JsonLineStream Client, JsonLineStream Server) SocketPair()
    {
        var (client, server) = RawSocketPair();
        return (
            new JsonLineStream(new NetworkStream(client, ownsSocket: true)),
            new JsonLineStream(new NetworkStream(server, ownsSocket: true)));
    }

    private static (Socket Client, Socket Server) RawSocketPair()
    {
        // A unix-domain socket pair, the transport the daemon listens on.
        var path = Path.Combine(Path.GetTempPath(), $"nm-ipc-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(path));
        var server = listener.Accept();
        File.Delete(path);
        return (client, server);
    }
}
