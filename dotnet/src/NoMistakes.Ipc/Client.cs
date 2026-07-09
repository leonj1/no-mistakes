using System.Net.Sockets;

namespace NoMistakes.Ipc;

/// <summary>
/// A JSON-RPC error returned by the server. Mirrors Go's *ipc.RPCError, whose
/// Error() is just the message, so <see cref="Exception.Message"/> carries the
/// server-side error text callers match on (e.g. "no active run ...").
/// </summary>
public sealed class IpcRpcException : Exception
{
    public int Code { get; }

    public IpcRpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Connects to the IPC server over the unix-domain-socket transport and issues
/// JSON-RPC calls, one JSON document per newline-delimited frame. Ported from
/// Go internal/ipc client.go (Dial/Call/Close); Subscribe arrives with the
/// slice that ports event streaming.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly JsonLineStream stream;
    private readonly SemaphoreSlim mutex = new(1, 1); // serializes calls on a single connection

    /// <summary>Per-call response timeout (Go sets a 30s read deadline).</summary>
    internal TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private IpcClient(JsonLineStream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Connects to the IPC server at the given socket path. Throws
    /// <see cref="IOException"/> ("dial ipc: ...") when nothing is listening.
    /// </summary>
    public static async Task<IpcClient> DialAsync(string socketPath, CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new IOException($"dial ipc: {ex.Message}", ex);
        }
        return new IpcClient(new JsonLineStream(new NetworkStream(socket, ownsSocket: true)));
    }

    /// <summary>
    /// Sends a JSON-RPC request and waits for the response, deserializing the
    /// result. A JSON-RPC error from the server surfaces as
    /// <see cref="IpcRpcException"/>; transport failures as
    /// <see cref="IOException"/>.
    /// </summary>
    public async Task<TResult?> CallAsync<TResult>(string method, object? parameters, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = Protocol.NewRequest(method, parameters);
            await stream.WriteAsync(request, ct).ConfigureAwait(false);

            Response? response;
            try
            {
                response = await stream.ReadAsync<Response>(ct).WaitAsync(CallTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new IOException("read response: timed out", ex);
            }
            if (response == null)
            {
                throw new IOException("read response: connection closed");
            }
            if (response.Error != null)
            {
                throw new IpcRpcException(response.Error.Code, response.Error.Message);
            }
            if (response.Result is { } result)
            {
                return IpcJson.Deserialize<TResult>(result);
            }
            return default;
        }
        finally
        {
            mutex.Release();
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        mutex.Dispose();
    }
}
