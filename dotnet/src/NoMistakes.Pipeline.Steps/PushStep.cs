using NoMistakes.Core;
using NoMistakes.Git;
using NoMistakes.Pipeline;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Force-pushes the worktree state to the configured push remote. Ports Go's
/// steps.PushStep. Every force-push routes through <see cref="ForcePush.ResolveDecisionAsync"/>
/// so an out-of-band or stale-mirror commit fails loudly instead of being
/// silently dropped. Fork routing uses <see cref="Repo.PushUrl"/>.
///
/// The in-repo test-evidence staging (Go's stageInRepoEvidence) is deferred to
/// the evidence slice; the safety-critical push routing is complete here.
/// </summary>
public sealed class PushStep : IStep
{
    private readonly GitClient git = new();

    public string Name => StepName.Push;

    public async Task<StepOutcome> ExecuteAsync(StepContext sctx)
    {
        var newHeadSha = string.Empty;

        var fmtCmd = sctx.Config?.Commands.Format ?? string.Empty;
        if (fmtCmd.Length > 0)
        {
            sctx.Log($"running formatter: {fmtCmd}");
            try
            {
                var (output, exitCode) = await StepHelpers.RunShellCommandAsync(sctx, fmtCmd).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    sctx.Log($"warning: format command exited with code {exitCode}: {output}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sctx.Log($"warning: format command failed: {ex.Message}");
            }
        }

        var status = await GitTry(sctx, "status", "--porcelain").ConfigureAwait(false);
        if (status.Trim().Length > 0)
        {
            sctx.Log("committing agent changes...");
            await git.RunAsync(sctx.WorkDir, new[] { "add", "-A" }, sctx.Ct).ConfigureAwait(false);
            await git.RunAsync(sctx.WorkDir, new[] { "commit", "-m", "no-mistakes: apply agent fixes" }, sctx.Ct).ConfigureAwait(false);
            newHeadSha = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();
        }

        var @ref = StepHelpers.NormalizedBranchRef(sctx.Run.Branch);
        var branch = @ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? @ref["refs/heads/".Length..] : @ref;

        var pushUrl = sctx.Repo.PushUrl();
        var pushTarget = "upstream";
        var usingFork = sctx.Repo.ForkUrl.Trim().Length > 0;
        if (usingFork)
        {
            pushTarget = "fork";
            sctx.Log($"pushing to fork ({@ref})...");
        }
        else
        {
            sctx.Log($"pushing to upstream ({@ref})...");
        }

        var headBeingPushed = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();

        // Decide whether force-pushing would discard commits the pipeline never saw.
        // The lease is anchored to the remote-tracking ref the rebase step freshly
        // fetched, so a push that would clobber an out-of-band or stale-mirror commit
        // fails loudly instead of silently dropping it.
        var lastSeen = await StepHelpers.LastFetchedBranchTipAsync(sctx, branch, usingFork).ConfigureAwait(false);
        GitRunner gitRun = args => git.RunAsync(sctx.WorkDir, args, sctx.Ct);
        ForcePushDecision decision;
        try
        {
            decision = await ForcePush.ResolveDecisionAsync(gitRun, pushUrl, @ref, headBeingPushed, lastSeen, sctx.Run.BaseSha)
                .ConfigureAwait(false);
        }
        catch (ForcePushWouldDiscardException)
        {
            // The guard refuses: re-throw so the executor fails the step loudly
            // rather than clobbering upstream work.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"push to {pushTarget}: {ex.Message}", ex);
        }

        if (decision.NewBranch)
        {
            await git.PushAsync(sctx.WorkDir, pushUrl, @ref, string.Empty, false, sctx.Ct).ConfigureAwait(false);
        }
        else if (decision.UpToDate)
        {
            // Remote already at this head: nothing to push, just reconcile refs below.
        }
        else
        {
            await git.PushAsync(sctx.WorkDir, pushUrl, @ref, decision.RemoteSha, true, sctx.Ct).ConfigureAwait(false);
        }

        if (newHeadSha.Length > 0)
        {
            await git.RunAsync(sctx.WorkDir, new[] { "update-ref", @ref, newHeadSha }, sctx.Ct).ConfigureAwait(false);
        }

        var headSha = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();
        if (headSha != sctx.Run.HeadSha)
        {
            sctx.Run.HeadSha = headSha;
            sctx.Db.UpdateRunHeadSha(sctx.Run.Id, headSha);
        }

        sctx.Log("pushed successfully");
        return new StepOutcome();
    }

    private async Task<string> GitTry(StepContext sctx, params string[] args)
    {
        try
        {
            return await git.RunAsync(sctx.WorkDir, args, sctx.Ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            return string.Empty;
        }
    }
}
