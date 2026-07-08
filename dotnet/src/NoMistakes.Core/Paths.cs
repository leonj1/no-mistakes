namespace NoMistakes.Core;

/// <summary>
/// Paths provides access to all no-mistakes filesystem locations. The root
/// defaults to ~/.no-mistakes but can be overridden via NM_HOME or by using
/// <see cref="WithRoot"/> (for testing). Ported from Go's internal/paths.
/// </summary>
public sealed class Paths
{
    private readonly string root;

    private Paths(string root)
    {
        this.root = root;
    }

    /// <summary>Returns Paths rooted at NM_HOME or ~/.no-mistakes.</summary>
    public static Paths New()
    {
        var env = Environment.GetEnvironmentVariable("NM_HOME");
        if (!string.IsNullOrEmpty(env))
        {
            return new Paths(env);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }
        if (string.IsNullOrEmpty(home))
        {
            throw new InvalidOperationException("cannot determine user home directory");
        }

        return new Paths(Path.Combine(home, ".no-mistakes"));
    }

    /// <summary>Returns Paths rooted at a custom directory (for testing).</summary>
    public static Paths WithRoot(string root) => new(root);

    public string Root => root;

    public string Db => Path.Combine(root, "state.sqlite");

    public string Socket => Path.Combine(root, "socket");

    public string PidFile => Path.Combine(root, "daemon.pid");

    public string ConfigFile => Path.Combine(root, "config.yaml");

    public string UpdateCheckFile => Path.Combine(root, "update-check.json");

    public string ReposDir => Path.Combine(root, "repos");

    public string RepoDir(string repoId) => Path.Combine(root, "repos", repoId + ".git");

    public string WorktreesDir => Path.Combine(root, "worktrees");

    public string WorktreeDir(string repoId, string runId) => Path.Combine(root, "worktrees", repoId, runId);

    public string LogsDir => Path.Combine(root, "logs");

    public string RunLogDir(string runId) => Path.Combine(root, "logs", runId);

    public string DaemonLog => Path.Combine(root, "logs", "daemon.log");

    public string CliLog => Path.Combine(root, "logs", "cli.log");

    /// <summary>
    /// ServerPidsDir holds PID-tracking files for legacy managed agent servers so a
    /// freshly started daemon can reap orphans left behind by a crashed predecessor.
    /// </summary>
    public string ServerPidsDir => Path.Combine(root, "servers");

    /// <summary>Creates all required directories under root.</summary>
    public void EnsureDirs()
    {
        foreach (var dir in new[] { root, ReposDir, WorktreesDir, LogsDir, ServerPidsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
