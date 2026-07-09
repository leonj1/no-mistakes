using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Ipc;

/// <summary>JSON-RPC 2.0 method names. Mirrors Go internal/ipc protocol.go.</summary>
public static class Methods
{
    /// <summary>Sent by the post-receive hook's `daemon notify-push` command when a push arrives.</summary>
    public const string PushReceived = "push_received";
    public const string GetRun = "get_run";
    public const string GetRuns = "get_runs";
    public const string GetActiveRun = "get_active_run";
    public const string Rerun = "rerun";
    public const string Subscribe = "subscribe";
    public const string Respond = "respond";
    public const string CancelRun = "cancel_run";
    public const string Health = "health";
    public const string Shutdown = "shutdown";
}

/// <summary>JSON-RPC 2.0 error codes.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;
}

/// <summary>A JSON-RPC 2.0 request.</summary>
public sealed class Request
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }
}

/// <summary>A JSON-RPC 2.0 response.</summary>
public sealed class Response
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }
}

/// <summary>A JSON-RPC 2.0 error object.</summary>
public sealed class RpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>Factories for protocol messages. Mirrors Go's NewRequest/NewResponse/NewErrorResponse.</summary>
public static class Protocol
{
    private static long requestId;

    /// <summary>Creates a JSON-RPC 2.0 request with an auto-incremented ID.</summary>
    public static Request NewRequest(string method, object? parameters) => new()
    {
        Jsonrpc = "2.0",
        Method = method,
        Params = IpcJson.ToElement(parameters),
        Id = Interlocked.Increment(ref requestId),
    };

    /// <summary>Creates a successful JSON-RPC 2.0 response.</summary>
    public static Response NewResponse(long id, object? result) => new()
    {
        Jsonrpc = "2.0",
        Result = IpcJson.ToElement(result),
        Id = id,
    };

    /// <summary>Creates an error JSON-RPC 2.0 response.</summary>
    public static Response NewErrorResponse(long id, int code, string message) => new()
    {
        Jsonrpc = "2.0",
        Error = new RpcError { Code = code, Message = message },
        Id = id,
    };
}
