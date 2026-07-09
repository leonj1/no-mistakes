using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using NoMistakes.Git;
using NoMistakes.Ipc;

namespace NoMistakes.Cli;

/// <summary>
/// Bundles the resources an axi subcommand needs: resolved paths, an open
/// database, the repo for the current directory, and - for commands that
/// mutate run state - a connection to the daemon. Ports Go's cli.axiEnv /
/// openAxiEnv / findRepo.
/// </summary>
public sealed class AxiEnv : IDisposable
{
    public Paths Paths { get; }
    public Database Db { get; }
    public Repo Repo { get; }

    /// <summary>
    /// Open connection to the daemon; non-null exactly when the env was opened
    /// with <c>ensureDaemonConn</c>.
    /// </summary>
    public IpcClient? Client { get; private set; }

    private AxiEnv(Paths paths, Database db, Repo repo)
    {
        Paths = paths;
        Db = db;
        Repo = repo;
    }

    /// <summary>
    /// Resolves paths, opens the database, and finds the repo for the current
    /// directory. Errors are thrown for the caller to render as structured TOON.
    /// </summary>
    public static Task<AxiEnv> OpenAsync(CancellationToken ct = default) => OpenAsync(false, ct);

    /// <summary>
    /// Like <see cref="OpenAsync(CancellationToken)"/>, and when
    /// <paramref name="ensureDaemonConn"/> is true also requires the daemon and
    /// dials it, populating <see cref="Client"/>. Go's openAxiEnv spawns the
    /// daemon on demand here; the .NET daemon bootstrap command has not landed
    /// yet, so a stopped daemon is an error until that slice arrives.
    /// </summary>
    public static async Task<AxiEnv> OpenAsync(bool ensureDaemonConn, CancellationToken ct = default)
    {
        var paths = Paths.New();
        paths.EnsureDirs();
        var db = Database.Open(paths.Db);
        AxiEnv env;
        try
        {
            var repo = await FindRepoAsync(db, ct).ConfigureAwait(false);
            env = new AxiEnv(paths, db, repo);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        if (ensureDaemonConn)
        {
            try
            {
                if (!await DaemonStatus.IsRunningAsync(paths, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("start daemon: daemon is not running");
                }
                env.Client = await DialAsync(paths, ct).ConfigureAwait(false);
            }
            catch
            {
                env.Dispose();
                throw;
            }
        }
        return env;
    }

    private static async Task<IpcClient> DialAsync(Paths paths, CancellationToken ct)
    {
        try
        {
            return await IpcClient.DialAsync(paths.Socket, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new IOException($"connect to daemon: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Looks up the repo for the current directory. If the working directory is
    /// inside a git worktree, it falls back to the main repository root so that
    /// worktrees work out of the box when the main repo is already initialized.
    /// Ports Go's cli.findRepo.
    /// </summary>
    internal static async Task<Repo> FindRepoAsync(Database db, CancellationToken ct = default)
    {
        var git = new GitClient();
        string gitRoot;
        try
        {
            gitRoot = await git.FindGitRootAsync(".", ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            throw new InvalidOperationException("not in a git repository");
        }
        var repo = db.GetRepoByPath(gitRoot);
        if (repo != null)
        {
            return repo;
        }
        // Try the main worktree root (handles git worktrees).
        string mainRoot;
        try
        {
            mainRoot = await git.FindMainRepoRootAsync(".", ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            mainRoot = gitRoot;
        }
        if (mainRoot == gitRoot)
        {
            throw new InvalidOperationException("repo not initialized (run 'no-mistakes init' first)");
        }
        repo = db.GetRepoByPath(mainRoot);
        if (repo == null)
        {
            throw new InvalidOperationException("repo not initialized (run 'no-mistakes init' first)");
        }
        return repo;
    }

    /// <summary>
    /// The current branch used for run resolution, or "" when it cannot be
    /// determined (not a repo, detached HEAD). Ports Go's
    /// currentBranchForRunResolve.
    /// </summary>
    public static async Task<string> CurrentBranchForRunResolveAsync(CancellationToken ct = default)
    {
        try
        {
            var branch = (await new GitClient().CurrentBranchAsync(".", ct).ConfigureAwait(false)).Trim();
            return branch == "HEAD" ? "" : branch;
        }
        catch (GitCommandException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        Client?.Dispose();
        Db.Dispose();
    }
}
