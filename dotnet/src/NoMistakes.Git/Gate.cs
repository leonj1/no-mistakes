namespace NoMistakes.Git;

/// <summary>
/// Constants for the local gate repository. Ports the pieces of Go's
/// internal/gate the CLI needs; gate setup/refresh itself lands with the init
/// slice.
/// </summary>
public static class Gate
{
    /// <summary>
    /// The name of the git remote in a working repository that points to the
    /// local gate. Mirrors Go's gate.RemoteName.
    /// </summary>
    public const string RemoteName = "no-mistakes";
}
