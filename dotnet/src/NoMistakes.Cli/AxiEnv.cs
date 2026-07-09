using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Git;

namespace NoMistakes.Cli;

/// <summary>
/// Bundles the resources an axi subcommand needs: resolved paths, an open
/// database, and the repo for the current directory. Ports Go's cli.axiEnv /
/// openAxiEnv / findRepo (read-only half; the ensure-daemon half arrives with
/// the mutating commands in slice 8c).
/// </summary>
public sealed class AxiEnv : IDisposable
{
    public Paths Paths { get; }
    public Database Db { get; }
    public Repo Repo { get; }

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
    public static async Task<AxiEnv> OpenAsync(CancellationToken ct = default)
    {
        var paths = Paths.New();
        paths.EnsureDirs();
        var db = Database.Open(paths.Db);
        try
        {
            var repo = await FindRepoAsync(db, ct).ConfigureAwait(false);
            return new AxiEnv(paths, db, repo);
        }
        catch
        {
            db.Dispose();
            throw;
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

    public void Dispose() => Db.Dispose();
}
