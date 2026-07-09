namespace NoMistakes.Processes;

/// <summary>
/// Immutable description of a subprocess to run through <see cref="ShellCommand"/>:
/// the executable, its arguments, and optional working directory, environment
/// overrides, and pipe-backstop delay. Kept separate from the runner so a spec
/// can be constructed and inspected independently (mirrors how Go builds an
/// <c>exec.Cmd</c> before <c>ConfigureShellCommand</c>).
/// </summary>
public sealed record ShellCommandSpec
{
    public ShellCommandSpec(string fileName, params string[] arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    /// <summary>The executable to run (resolved via PATH by the OS).</summary>
    public string FileName { get; init; }

    /// <summary>Arguments passed to the executable, unquoted.</summary>
    public IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory, or null to inherit the parent's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment overrides layered onto the inherited environment. Null leaves
    /// the environment inherited unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Ceiling for waiting on captured-output reads after the leader exits; null
    /// uses <see cref="ShellCommand.DefaultWaitDelay"/>. A short value bounds a
    /// probe that may leave an inherited-pipe holder behind.
    /// </summary>
    public TimeSpan? WaitDelay { get; init; }
}
