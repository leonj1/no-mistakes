using System.Text.Json;
using NoMistakes.Core;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports of Go internal/ipc/protocol_test.go: JSON shape and round-trips of
/// the JSON-RPC protocol types.
/// </summary>
public class IpcProtocolTests
{
    [Fact]
    public void RequestMarshalRoundTrip()
    {
        var req = new Request { Jsonrpc = "2.0", Method = Methods.Health, Id = 1 };
        var got = RoundTrip(req);
        Assert.Equal("2.0", got.Jsonrpc);
        Assert.Equal(Methods.Health, got.Method);
        Assert.Equal(1, got.Id);
    }

    [Fact]
    public void RequestWithParamsRoundTrip()
    {
        var parameters = new PushReceivedParams
        {
            Gate = "/home/user/.no-mistakes/repos/abc123.git",
            Ref = "refs/heads/feature",
            Old = "0000000000000000000000000000000000000000",
            New = "abc123def456",
        };
        var req = new Request
        {
            Jsonrpc = "2.0",
            Method = Methods.PushReceived,
            Params = IpcJson.ToElement(parameters),
            Id = 42,
        };
        var got = RoundTrip(req);
        Assert.NotNull(got.Params);
        var gotParams = IpcJson.Deserialize<PushReceivedParams>(got.Params.Value)!;
        Assert.Equal(parameters.Gate, gotParams.Gate);
        Assert.Equal(parameters.Ref, gotParams.Ref);
    }

    [Fact]
    public void ResponseSuccessRoundTrip()
    {
        var resp = new Response
        {
            Jsonrpc = "2.0",
            Result = IpcJson.ToElement(new HealthResult { Status = "ok" }),
            Id = 1,
        };
        var got = RoundTrip(resp);
        Assert.Null(got.Error);
        Assert.NotNull(got.Result);
        var result = IpcJson.Deserialize<HealthResult>(got.Result.Value)!;
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public void ResponseErrorRoundTrip()
    {
        var resp = new Response
        {
            Jsonrpc = "2.0",
            Error = new RpcError { Code = ErrorCodes.MethodNotFound, Message = "method not found" },
            Id = 1,
        };
        var got = RoundTrip(resp);
        Assert.NotNull(got.Error);
        Assert.Equal(ErrorCodes.MethodNotFound, got.Error!.Code);
        Assert.Equal("method not found", got.Error.Message);
    }

    [Fact]
    public void PushReceivedParamsRoundTrip()
    {
        var parameters = new PushReceivedParams
        {
            Gate = "/path/to/gate.git",
            Ref = "refs/heads/main",
            Old = "aaa",
            New = "bbb",
            SkipSteps = new List<string> { StepName.Test, StepName.Lint },
        };
        var got = RoundTrip(parameters);
        Assert.Equal(parameters.Gate, got.Gate);
        Assert.Equal(parameters.Ref, got.Ref);
        Assert.Equal(parameters.Old, got.Old);
        Assert.Equal(parameters.New, got.New);
        Assert.Equal(parameters.SkipSteps, got.SkipSteps);
    }

    [Fact]
    public void RerunParamsRoundTrip()
    {
        var parameters = new RerunParams
        {
            RepoId = "repo456",
            Branch = "feature",
            SkipSteps = new List<string> { StepName.Review },
        };
        var got = RoundTrip(parameters);
        Assert.Equal("repo456", got.RepoId);
        Assert.Equal("feature", got.Branch);
        Assert.Equal(new List<string> { StepName.Review }, got.SkipSteps);
    }

    [Fact]
    public void CancelRunParamsRoundTrip()
    {
        var got = RoundTrip(new CancelRunParams { RunId = "01ABCDEF" });
        Assert.Equal("01ABCDEF", got.RunId);
    }

    [Fact]
    public void RespondParamsRoundTripIncludingAddedFindings()
    {
        var parameters = new RespondParams
        {
            RunId = "run001",
            Step = StepName.Review,
            Action = "fix",
            FindingIds = new List<string> { "f1", "f2" },
            Instructions = new Dictionary<string, string> { ["f1"] = "keep the guard clause" },
            AddedFindings = new List<Finding>
            {
                new()
                {
                    Id = "u1",
                    Severity = "blocking",
                    File = "main.go",
                    Line = 10,
                    Description = "user-added finding",
                    Action = "auto_fix",
                    UserInstructions = "rename it",
                },
            },
        };
        var json = IpcJson.Serialize(parameters);
        var got = IpcJson.Deserialize<RespondParams>(json)!;
        Assert.Equal("run001", got.RunId);
        Assert.Equal(StepName.Review, got.Step);
        Assert.Equal("fix", got.Action);
        Assert.Equal(new List<string> { "f1", "f2" }, got.FindingIds);
        Assert.Equal("keep the guard clause", got.Instructions!["f1"]);
        var finding = Assert.Single(got.AddedFindings!);
        Assert.Equal("u1", finding.Id);
        Assert.Equal("blocking", finding.Severity);
        Assert.Equal("user-added finding", finding.Description);
        Assert.Equal("rename it", finding.UserInstructions);

        // The finding must use Go's snake_case json tags on the wire.
        Assert.Contains("\"user_instructions\":\"rename it\"", json);
        Assert.Contains("\"description\":\"user-added finding\"", json);
    }

    [Fact]
    public void StepNamesNormalizeBabysitOnRead()
    {
        var respond = IpcJson.Deserialize<RespondParams>(
            "{\"run_id\":\"r1\",\"step\":\"babysit\",\"action\":\"approve\"}")!;
        Assert.Equal(StepName.Ci, respond.Step);

        var push = IpcJson.Deserialize<PushReceivedParams>(
            "{\"gate\":\"/g\",\"ref\":\"refs/heads/x\",\"old\":\"a\",\"new\":\"b\",\"skip_steps\":[\"babysit\",\"test\"]}")!;
        Assert.Equal(new List<string> { StepName.Ci, StepName.Test }, push.SkipSteps);

        var step = IpcJson.Deserialize<StepResultInfo>(
            "{\"id\":\"s1\",\"run_id\":\"r1\",\"step_name\":\"babysit\",\"step_order\":9,\"status\":\"running\"}")!;
        Assert.Equal(StepName.Ci, step.StepName);
    }

    [Fact]
    public void RunInfoNullableFieldsOmitted()
    {
        var info = new RunInfo
        {
            Id = "run001",
            RepoId = "repo001",
            Branch = "main",
            HeadSha = "abc",
            BaseSha = "def",
            Status = RunStatus.Pending,
            CreatedAt = 1700000000,
            UpdatedAt = 1700000000,
        };
        using var doc = JsonDocument.Parse(IpcJson.Serialize(info));
        Assert.False(doc.RootElement.TryGetProperty("pr_url", out _));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.False(doc.RootElement.TryGetProperty("awaiting_agent", out _));
        Assert.False(doc.RootElement.TryGetProperty("awaiting_agent_since", out _));
        Assert.False(doc.RootElement.TryGetProperty("steps", out _));
    }

    [Fact]
    public void RunInfoAwaitingAgentRoundTrip()
    {
        var info = new RunInfo
        {
            Id = "run001",
            RepoId = "repo001",
            Branch = "main",
            HeadSha = "abc",
            BaseSha = "def",
            Status = RunStatus.Running,
            AwaitingAgent = true,
            AwaitingAgentSince = 1700000123,
            CreatedAt = 1700000000,
            UpdatedAt = 1700000000,
        };
        var got = RoundTrip(info);
        Assert.True(got.AwaitingAgent);
        Assert.Equal(1700000123L, got.AwaitingAgentSince);
    }

    [Fact]
    public void StepResultInfoRoundTrip()
    {
        var step = new StepResultInfo
        {
            Id = "s1",
            RunId = "r1",
            StepName = StepName.Test,
            StepOrder = 2,
            Status = StepStatus.Completed,
            ExitCode = 0,
            DurationMs = 1234,
            FixSummaries = new List<string> { "fixed the flake", "" },
        };
        var got = RoundTrip(step);
        Assert.Equal(StepName.Test, got.StepName);
        Assert.Equal(2, got.StepOrder);
        Assert.Equal(0, got.ExitCode);
        Assert.Equal(1234L, got.DurationMs);
        Assert.Equal(new List<string> { "fixed the flake", "" }, got.FixSummaries);
    }

    [Fact]
    public void EventRoundTrip()
    {
        var evt = new IpcEvent
        {
            Type = EventTypes.RunCompleted,
            RunId = "run001",
            RepoId = "repo001",
            Status = RunStatus.Failed,
            Error = "step review failed",
        };
        var got = RoundTrip(evt);
        Assert.Equal(EventTypes.RunCompleted, got.Type);
        Assert.Equal("run001", got.RunId);
        Assert.Equal("step review failed", got.Error);
    }

    [Fact]
    public void MethodConstantsAreUnique()
    {
        var methods = new[]
        {
            Methods.PushReceived, Methods.GetRun, Methods.GetRuns, Methods.GetActiveRun,
            Methods.Rerun, Methods.Subscribe, Methods.Respond, Methods.CancelRun,
            Methods.Health, Methods.Shutdown,
        };
        Assert.Equal(10, methods.Length);
        Assert.All(methods, m => Assert.False(string.IsNullOrEmpty(m)));
        Assert.Equal(methods.Length, methods.Distinct().Count());
    }

    [Fact]
    public void ErrorCodesAreUnique()
    {
        var codes = new[]
        {
            ErrorCodes.ParseError, ErrorCodes.InvalidRequest, ErrorCodes.MethodNotFound,
            ErrorCodes.InvalidParams, ErrorCodes.Internal,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Fact]
    public void NewRequestAssignsIncrementingIds()
    {
        var first = Protocol.NewRequest(Methods.Health, new HealthParams());
        var second = Protocol.NewRequest(Methods.Health, new HealthParams());
        Assert.Equal("2.0", first.Jsonrpc);
        Assert.Equal(Methods.Health, first.Method);
        Assert.NotEqual(0, first.Id);
        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public void NewResponseShape()
    {
        var resp = Protocol.NewResponse(42, new HealthResult { Status = "ok" });
        Assert.Equal("2.0", resp.Jsonrpc);
        Assert.Equal(42, resp.Id);
        Assert.Null(resp.Error);
        Assert.NotNull(resp.Result);
    }

    [Fact]
    public void NewErrorResponseShape()
    {
        var resp = Protocol.NewErrorResponse(42, ErrorCodes.MethodNotFound, "unknown method");
        Assert.Equal("2.0", resp.Jsonrpc);
        Assert.Equal(42, resp.Id);
        Assert.NotNull(resp.Error);
        Assert.Equal(ErrorCodes.MethodNotFound, resp.Error!.Code);
        Assert.Equal("unknown method", resp.Error.Message);
        Assert.Null(resp.Result);
    }

    private static T RoundTrip<T>(T value)
        where T : class
    {
        return IpcJson.Deserialize<T>(IpcJson.Serialize(value))!;
    }
}
