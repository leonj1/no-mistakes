using System.Net.Sockets;
using System.Text.Json;

namespace NoMistakes.Ipc;

/// <summary>Processes a JSON-RPC request and returns a result. Mirrors Go's ipc.HandlerFunc.</summary>
public delegate Task<object?> IpcHandler(JsonElement? parameters, CancellationToken ct);

/// <summary>
/// Takes over a connection for streaming. send writes one JSON object to the
/// connection; the handler blocks until streaming is complete. Mirrors Go's
/// ipc.StreamHandlerFunc.
/// </summary>
public delegate Task IpcStreamHandler(JsonElement? parameters, Func<object?, Task> send, CancellationToken ct);

/// <summary>
/// Listens on a unix-domain socket and dispatches JSON-RPC requests, one JSON
/// document per newline-delimited frame. Ported from Go internal/ipc server.go
/// plus the unix listen() transport (stale socket file removed before bind,
/// socket file restricted to the owning user).
/// </summary>
public sealed class IpcServer
{
    private readonly object gate = new();
    private readonly Dictionary<string, IpcHandler> handlers = new();
    private readonly Dictionary<string, IpcStreamHandler> streamHandlers = new();
    private readonly CancellationTokenSource done = new();
    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? listener;

    /// <summary>Completes once the listener is bound and accepting, faults if binding fails.</summary>
    public Task Listening => listening.Task;

    /// <summary>Registers a handler for a JSON-RPC method.</summary>
    public void Handle(string method, IpcHandler handler)
    {
        lock (gate)
        {
            handlers[method] = handler;
        }
    }

    /// <summary>
    /// Registers a streaming handler. The server sends an initial OK response,
    /// then hands the connection to the handler; the connection closes when the
    /// handler returns.
    /// </summary>
    public void HandleStream(string method, IpcStreamHandler handler)
    {
        lock (gate)
        {
            streamHandlers[method] = handler;
        }
    }

    /// <summary>
    /// Listens on the given socket path and serves connections until
    /// <see cref="Close"/> is called, then returns after in-flight connections
    /// have drained.
    /// </summary>
    public async Task ServeAsync(string socketPath)
    {
        var ln = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // A previous daemon may have crashed without removing its socket
            // file; bind fails on the stale leftover, so clear it first
            // (Go transport_unix.go does the same os.Remove before listen).
            File.Delete(socketPath);
            ln.Bind(new UnixDomainSocketEndPoint(socketPath));
            ln.Listen(16);
            if (!OperatingSystem.IsWindows())
            {
                // Go listens under umask 0o077 so only the owner can connect.
                File.SetUnixFileMode(
                    socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            ln.Dispose();
            listening.TrySetException(ex);
            throw;
        }

        lock (gate)
        {
            listener = ln;
        }
        listening.TrySetResult();

        var connections = new List<Task>();
        try
        {
            while (true)
            {
                Socket conn;
                try
                {
                    conn = await ln.AcceptAsync(done.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // Close() was called
                }
                catch (Exception) when (done.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Listener torn down out from under us (CloseListener):
                    // treat like Go's net.ErrClosed and shut down cleanly.
                    Close();
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Close();
                    break;
                }

                lock (gate)
                {
                    connections.RemoveAll(t => t.IsCompleted);
                    connections.Add(HandleConnAsync(conn));
                }
            }
        }
        finally
        {
            ln.Dispose();
        }

        Task[] pending;
        lock (gate)
        {
            pending = connections.ToArray();
        }
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Connection handlers own their error reporting.
            }
        }
    }

    /// <summary>Gracefully shuts down the server. Idempotent.</summary>
    public void Close()
    {
        try
        {
            done.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Closes the underlying listener without signaling shutdown; the accept
    /// loop detects the closed listener and exits cleanly (Go CloseListener).
    /// </summary>
    public void CloseListener()
    {
        Socket? ln;
        lock (gate)
        {
            ln = listener;
        }
        ln?.Dispose();
    }

    private async Task HandleConnAsync(Socket conn)
    {
        using var stream = new JsonLineStream(new NetworkStream(conn, ownsSocket: true));
        while (true)
        {
            Request? request;
            try
            {
                request = await stream.ReadAsync<Request>(done.Token).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // The malformed line has been consumed; report and keep reading.
                if (!await TryWriteAsync(stream, Protocol.NewErrorResponse(0, ErrorCodes.ParseError, "invalid json")).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }
            catch (Exception)
            {
                return; // client vanished mid-message, oversized frame, or shutdown
            }
            if (request == null)
            {
                return; // clean disconnect
            }

            IpcStreamHandler? streamHandler;
            lock (gate)
            {
                streamHandlers.TryGetValue(request.Method, out streamHandler);
            }
            if (streamHandler != null)
            {
                // Initial OK response, then the handler owns the connection.
                var ok = Protocol.NewResponse(request.Id, new Dictionary<string, bool> { ["ok"] = true });
                if (!await TryWriteAsync(stream, ok).ConfigureAwait(false))
                {
                    return;
                }
                try
                {
                    await streamHandler(
                        request.Params,
                        ev => stream.WriteAsync(ev, done.Token),
                        done.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Stream ended (client disconnected or shutdown).
                }
                return; // connection done after streaming
            }

            var response = await DispatchAsync(request).ConfigureAwait(false);
            if (!await TryWriteAsync(stream, response).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<Response> DispatchAsync(Request request)
    {
        IpcHandler? handler;
        lock (gate)
        {
            handlers.TryGetValue(request.Method, out handler);
        }
        if (handler == null)
        {
            return Protocol.NewErrorResponse(request.Id, ErrorCodes.MethodNotFound, "method not found: " + request.Method);
        }
        try
        {
            var result = await handler(request.Params, done.Token).ConfigureAwait(false);
            return Protocol.NewResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            return Protocol.NewErrorResponse(request.Id, ErrorCodes.Internal, ex.Message);
        }
    }

    // Responses are written without the shutdown token so a reply produced
    // just before Close() (the shutdown method's own OK) still reaches the
    // client before the connection is torn down.
    private static async Task<bool> TryWriteAsync(JsonLineStream stream, object message)
    {
        try
        {
            await stream.WriteAsync(message).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
