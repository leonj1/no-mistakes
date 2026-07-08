namespace NoMistakes.Config;

/// <summary>The merged result of global + per-repo configuration.</summary>
public sealed class Config
{
    /// <summary>Monitor idle timeout used when ci_timeout is unset (7 days).</summary>
    public static readonly TimeSpan DefaultCiTimeout = TimeSpan.FromHours(168);

    /// <summary>
    /// Sentinel meaning "monitor until the PR is merged, closed, or the run is
    /// aborted - never self-terminate". Any non-positive ci_timeout, or the
    /// keywords "unlimited"/"none"/"off"/"never", resolves to this.
    /// </summary>
    public static readonly TimeSpan CiTimeoutUnlimited = TimeSpan.FromTicks(-1);

    internal static readonly IReadOnlyDictionary<string, string> DefaultBinary = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AgentNames.Claude] = "claude",
        [AgentNames.Codex] = "codex",
        [AgentNames.Pi] = "pi",
        [AgentNames.Copilot] = "copilot",
        [AgentNames.Droid] = "droid",
    };

    public string Agent { get; set; } = string.Empty;
    public List<string> Agents { get; set; } = new();
    public string AcpxPath { get; set; } = string.Empty;
    public Dictionary<string, string>? AcpRegistryOverrides { get; set; }
    public Dictionary<string, string>? AgentPathOverride { get; set; }
    public Dictionary<string, List<string>>? AgentArgsOverride { get; set; }
    public TimeSpan CiTimeout { get; set; }
    public string LogLevel { get; set; } = string.Empty;
    public Commands Commands { get; set; } = new();
    public List<string> IgnorePatterns { get; set; } = new();
    public AutoFix AutoFix { get; set; }
    public Intent Intent { get; set; } = new();
    public Test Test { get; set; }

    /// <summary>True when the agent name is a valid acp:&lt;target&gt; selector.</summary>
    public static bool IsAcpAgent(string name)
    {
        if (!name.StartsWith("acp:", StringComparison.Ordinal))
        {
            return false;
        }
        var target = name.Substring("acp:".Length);
        return target.Length > 0 && target.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) < 0;
    }

    /// <summary>Returns the binary path for the configured agent.</summary>
    public string AgentPath() => AgentPathFor(Agent);

    /// <summary>
    /// Returns the binary path for the named agent. ACP agents use acpx_path if
    /// set, otherwise "acpx". Native agents use agent_path_override if set,
    /// otherwise the default binary name.
    /// </summary>
    public string AgentPathFor(string name)
    {
        if (IsAcpAgent(name))
        {
            return string.IsNullOrEmpty(AcpxPath) ? "acpx" : AcpxPath;
        }
        if (AgentPathOverride is not null && AgentPathOverride.TryGetValue(name, out var overridePath))
        {
            return overridePath;
        }
        if (DefaultBinary.TryGetValue(name, out var binary))
        {
            return binary;
        }
        return name;
    }

    /// <summary>Extra CLI args for the configured native agent, or null when unset.</summary>
    public IReadOnlyList<string>? AgentArgs() => AgentArgsFor(Agent);

    /// <summary>Extra CLI args for the named native agent, or null when unset.</summary>
    public IReadOnlyList<string>? AgentArgsFor(string name)
    {
        if (AgentArgsOverride is null)
        {
            return null;
        }
        return AgentArgsOverride.TryGetValue(name, out var args) ? args : null;
    }

    /// <summary>
    /// Returns the max auto-fix attempts for a given step. Steps without auto-fix
    /// support return 0.
    /// </summary>
    public int AutoFixLimit(string step) => step switch
    {
        StepNames.Lint => AutoFix.Lint,
        StepNames.Test => AutoFix.Test,
        StepNames.Review => AutoFix.Review,
        StepNames.Document => AutoFix.Document,
        StepNames.Ci => AutoFix.Ci,
        StepNames.Rebase => AutoFix.Rebase,
        _ => 0,
    };
}
