namespace NoMistakes.Config;

/// <summary>Optional per-repo command overrides.</summary>
public sealed class Commands
{
    public string Lint { get; set; } = string.Empty;
    public string Test { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

/// <summary>
/// YAML representation of auto-fix config. Nullable fields distinguish "not set"
/// (null) from "set to 0" (disabled).
/// </summary>
public sealed class AutoFixRaw
{
    public int? Lint { get; set; }
    public int? Test { get; set; }
    public int? Review { get; set; }
    public int? Document { get; set; }
    public int? Ci { get; set; }
    public int? Babysit { get; set; }
    public int? Rebase { get; set; }
}

/// <summary>
/// Resolved per-step auto-fix attempt limits. A value of 0 means auto-fix is
/// disabled (requires manual approval).
/// </summary>
public struct AutoFix
{
    public int Lint;
    public int Test;
    public int Review;
    public int Document;
    public int Ci;
    public int Rebase;
}

/// <summary>YAML representation of test-evidence settings.</summary>
public sealed class EvidenceRaw
{
    public bool? StoreInRepo { get; set; }
    public string? Dir { get; set; }
}

/// <summary>YAML representation of test-step settings.</summary>
public sealed class TestRaw
{
    public EvidenceRaw Evidence { get; set; } = new();
}

/// <summary>Resolved test-evidence config.</summary>
public struct Evidence
{
    public bool StoreInRepo;
    public string Dir;
}

/// <summary>Resolved test-step config.</summary>
public struct Test
{
    public Evidence Evidence;
}

/// <summary>YAML representation of user-intent extraction settings.</summary>
public sealed class IntentRaw
{
    public bool? Enabled { get; set; }
    public double? Threshold { get; set; }
    public int? SlackDays { get; set; }
    public List<string> DisabledReaders { get; set; } = new();
}

/// <summary>Resolved user-intent extraction config.</summary>
public sealed class Intent
{
    public bool Enabled { get; set; }
    public double Threshold { get; set; }
    public int SlackDays { get; set; }
    public Dictionary<string, bool> DisabledReaders { get; set; } = new();
}

/// <summary>Represents ~/.no-mistakes/config.yaml.</summary>
public sealed class GlobalConfig
{
    public string Agent { get; set; } = AgentNames.Auto;
    public List<string> Agents { get; set; } = new() { AgentNames.Auto };
    public string AcpxPath { get; set; } = string.Empty;
    public Dictionary<string, string>? AcpRegistryOverrides { get; set; }
    public Dictionary<string, string>? AgentPathOverride { get; set; }
    public Dictionary<string, List<string>>? AgentArgsOverride { get; set; }
    public TimeSpan CiTimeout { get; set; } = Config.DefaultCiTimeout;
    public string LogLevel { get; set; } = "info";
    public AutoFixRaw AutoFix { get; set; } = new();
    public IntentRaw Intent { get; set; } = new();
    public TestRaw Test { get; set; } = new();
}

/// <summary>Represents .no-mistakes.yaml in a repo root.</summary>
public sealed class RepoConfig
{
    public string Agent { get; set; } = string.Empty;
    public List<string> Agents { get; set; } = new();
    public Commands Commands { get; set; } = new();
    public List<string> IgnorePatterns { get; set; } = new();

    /// <summary>
    /// Opts in to honoring the code-executing selection fields
    /// (commands.{test,lint,format} and agent) from a contributor's pushed
    /// branch instead of the trusted default-branch copy. Read ONLY from the
    /// trusted default-branch copy (never the pushed SHA), so a contributor
    /// cannot self-enable. Defaults false.
    /// </summary>
    public bool AllowRepoCommands { get; set; }

    public AutoFixRaw AutoFix { get; set; } = new();
    public IntentRaw Intent { get; set; } = new();
    public TestRaw Test { get; set; } = new();
}
