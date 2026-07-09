namespace NoMistakes.Scm;

/// <summary>
/// Runs a provider CLI command (gh, glab, ...) and returns its captured
/// output. Mirrors Go's per-package <c>CmdFactory</c>: the implementation
/// owns working directory, environment, and process lifecycle; hosts only
/// describe the command line. <paramref name="stdin"/> is written to the
/// child's standard input when non-null (used to stream PR bodies without
/// hitting argv limits).
/// </summary>
public delegate Task<CommandResult> CommandRunner(
    string name, IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken);

/// <summary>
/// The captured outcome of a CLI command.
/// </summary>
public readonly record struct CommandResult(int ExitCode, string Stdout = "", string Stderr = "")
{
    public bool Success => ExitCode == 0;

    /// <summary>
    /// Stdout followed by stderr, standing in for Go's
    /// <c>cmd.CombinedOutput()</c> where the wrappers parse or report both
    /// streams together.
    /// </summary>
    public string CombinedOutput => Stdout + Stderr;
}

/// <summary>
/// A provider CLI command failed or produced unusable output. The message
/// starts with the command context (e.g. "glab mr list: ..."), mirroring the
/// Go wrappers' error strings.
/// </summary>
public sealed class ScmCommandException : Exception
{
    public ScmCommandException(string message) : base(message)
    {
    }
}
