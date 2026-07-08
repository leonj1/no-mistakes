namespace NoMistakes.Config;

/// <summary>Supported agent backend names. Mirrors Go's types.AgentName constants.</summary>
public static class AgentNames
{
    public const string Auto = "auto";
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Pi = "pi";
    public const string Copilot = "copilot";
    public const string Droid = "droid";
}

/// <summary>Pipeline step names. Mirrors Go's types.StepName constants.</summary>
public static class StepNames
{
    public const string Intent = "intent";
    public const string Rebase = "rebase";
    public const string Review = "review";
    public const string Test = "test";
    public const string Document = "document";
    public const string Lint = "lint";
    public const string Push = "push";
    public const string Pr = "pr";
    public const string Ci = "ci";
}

/// <summary>Resolved daemon log level. Mirrors the subset of slog levels no-mistakes uses.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Raised when configuration cannot be parsed or is invalid.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
