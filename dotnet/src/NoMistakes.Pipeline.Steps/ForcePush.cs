using NoMistakes.Git;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Runs a git subcommand and returns trimmed stdout, or throws on failure.
/// Callers bind it to the right working directory. Mirrors Go's gitRunner seam,
/// which lets the force-push safety logic be unit-tested with a fake runner.
/// </summary>
public delegate Task<string> GitRunner(params string[] args);

/// <summary>
/// How to push a head to a remote branch safely. Exactly one of NewBranch /
/// UpToDate is true, or neither (a guarded force-push anchored to RemoteSha is
/// required). Mirrors Go's forcePushDecision.
/// </summary>
public readonly record struct ForcePushDecision(string RemoteSha, bool NewBranch, bool UpToDate);

/// <summary>
/// Reports that a force-push would discard commits present on the remote branch
/// that the pipeline never incorporated. Refusing is the whole point: it keeps a
/// stale-base rebase or an out-of-band push from silently dropping work that
/// already landed upstream. Mirrors Go's forcePushWouldDiscardError.
/// </summary>
public sealed class ForcePushWouldDiscardException : Exception
{
    public string Ref { get; }
    public string RemoteSha { get; }
    public IReadOnlyList<string> Dropped { get; }

    public ForcePushWouldDiscardException(string @ref, string remoteSha, IReadOnlyList<string> dropped)
        : base(BuildMessage(@ref, remoteSha, dropped))
    {
        Ref = @ref;
        RemoteSha = remoteSha;
        Dropped = dropped;
    }

    private static string BuildMessage(string @ref, string remoteSha, IReadOnlyList<string> dropped)
    {
        var sample = dropped.Count > 5 ? dropped.Take(5).ToList() : dropped;
        return $"refusing to force-push {@ref}: remote head {ForcePush.ShortSha(remoteSha)} carries "
             + $"{dropped.Count} commit(s) the pipeline never incorporated (e.g. "
             + $"{string.Join(", ", sample.Select(ForcePush.ShortSha))}); pushing would discard upstream work. "
             + "Re-fetch and rebase onto the current remote, or push manually if this overwrite is intended.";
    }
}

/// <summary>
/// The force-push data-loss guard, ported from Go's forcepush.go. Every
/// force-push site routes through <see cref="ResolveDecisionAsync"/>, which
/// re-reads the live remote head and only allows the push when it cannot discard
/// commits the pipeline never observed.
/// </summary>
public static class ForcePush
{
    /// <summary>
    /// Re-reads the current state of <paramref name="ref"/> on the push remote and
    /// decides whether force-pushing <paramref name="newHeadSha"/> would discard
    /// commits the pipeline never saw. Throws when the push must NOT proceed —
    /// either git failed (fail safe rather than degrade to a blind --force) or the
    /// push would discard unseen upstream commits. Mirrors Go's
    /// resolveForcePushDecision.
    /// </summary>
    public static async Task<ForcePushDecision> ResolveDecisionAsync(
        GitRunner gitRun, string pushUrl, string @ref, string newHeadSha, string lastSeenSha, string baseSha)
    {
        string current;
        try
        {
            current = await LsRemoteShaAsync(gitRun, pushUrl, @ref).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ForcePushWouldDiscardException)
        {
            throw new InvalidOperationException($"resolve remote head for {@ref}: {ex.Message}", ex);
        }

        if (current.Length == 0)
        {
            return new ForcePushDecision(string.Empty, NewBranch: true, UpToDate: false);
        }
        if (current == newHeadSha)
        {
            return new ForcePushDecision(current, NewBranch: false, UpToDate: true);
        }
        if (lastSeenSha.Length > 0 && current == lastSeenSha)
        {
            // Remote unchanged since the pipeline last observed it: the force-push
            // only rewrites history we built on or last produced ourselves.
            return new ForcePushDecision(current, NewBranch: false, UpToDate: false);
        }

        // The remote moved to a commit we did not produce. Allow the push only if
        // everything now on the remote is already represented in what we are about
        // to push (or in the history the run is knowingly rewriting); otherwise
        // refuse rather than discard it.
        List<string> dropped;
        try
        {
            dropped = await RemoteCommitsNotIncorporatedAsync(gitRun, pushUrl, @ref, newHeadSha, current, baseSha)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ForcePushWouldDiscardException)
        {
            throw new InvalidOperationException($"verify force-push safety for {@ref}: {ex.Message}", ex);
        }

        if (dropped.Count == 0)
        {
            return new ForcePushDecision(current, NewBranch: false, UpToDate: false);
        }
        throw new ForcePushWouldDiscardException(@ref, current, dropped);
    }

    /// <summary>Returns the SHA a ref resolves to on a remote, or "" when absent.</summary>
    public static async Task<string> LsRemoteShaAsync(GitRunner gitRun, string remote, string @ref)
    {
        var outText = await gitRun("ls-remote", remote, @ref).ConfigureAwait(false);
        var fields = outText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 0 ? string.Empty : fields[0];
    }

    /// <summary>
    /// Returns the commits reachable from remoteSha whose changes are not already
    /// present (by patch-id) in newHeadSha and are not part of the history the run
    /// already knew (reachable from baseSha). Uses --cherry-pick so a clean rebase
    /// that rewrites SHAs but preserves patch-ids is recognized as incorporated,
    /// while a commit that only ever existed on the remote is flagged. Mirrors Go's
    /// remoteCommitsNotIncorporated.
    /// </summary>
    public static async Task<List<string>> RemoteCommitsNotIncorporatedAsync(
        GitRunner gitRun, string pushUrl, string @ref, string newHeadSha, string remoteSha, string baseSha)
    {
        var branch = StripHeadsPrefix(@ref);
        await gitRun("fetch", "--no-tags", pushUrl, "refs/heads/" + branch).ConfigureAwait(false);

        var args = new List<string> { "rev-list", "--cherry-pick", "--right-only", newHeadSha + "..." + remoteSha };
        if (baseSha.Length > 0 && !GitClient.IsZeroSha(baseSha))
        {
            try
            {
                await gitRun("rev-parse", "--verify", "--quiet", baseSha + "^{commit}").ConfigureAwait(false);
                args.Add("^" + baseSha);
            }
            catch (GitCommandException)
            {
                // baseSha not resolvable locally: omit it, which only makes the
                // check stricter (more likely to refuse), never laxer.
            }
        }

        var outText = await gitRun(args.ToArray()).ConfigureAwait(false);
        var commits = new List<string>();
        foreach (var raw in outText.Trim().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                commits.Add(line);
            }
        }
        return commits;
    }

    internal static string ShortSha(string sha) => sha.Length <= 12 ? sha : sha[..12];

    private static string StripHeadsPrefix(string @ref) =>
        @ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? @ref["refs/heads/".Length..] : @ref;
}
