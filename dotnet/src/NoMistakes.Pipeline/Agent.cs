namespace NoMistakes.Pipeline;

/// <summary>
/// Options for a single agent invocation. Mirrors Go's agent.RunOpts. The
/// concrete native-agent implementations land with slice 14; steps depend only
/// on this abstraction.
/// </summary>
public sealed class AgentRunOpts
{
    public required string Prompt { get; init; }
    public required string Cwd { get; init; }

    /// <summary>Structured-output JSON schema (optional).</summary>
    public string? JsonSchema { get; init; }

    /// <summary>Streaming text callback (optional).</summary>
    public Action<string>? OnChunk { get; init; }
}

/// <summary>The result of an agent invocation. Mirrors Go's agent.Result.</summary>
public sealed class AgentResult
{
    /// <summary>Structured JSON returned by the agent (null when none).</summary>
    public string? Output { get; init; }

    /// <summary>Raw text output.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// A driving agent (Claude, Codex, Pi, Copilot, Droid, ACP). Mirrors Go's
/// agent.Agent for the pipeline's purposes. The native implementations land with
/// slice 14; steps and tests target this interface.
/// </summary>
public interface IAgent
{
    Task<AgentResult> RunAsync(AgentRunOpts opts, CancellationToken ct);
}
