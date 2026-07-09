using NoMistakes.Core;
using NoMistakes.Git;
using NoMistakes.Pipeline;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Syncs the pushed branch with the configured push target and the latest default
/// branch from upstream. Ports Go's steps.RebaseStep.
///
/// Two data-loss invariants are preserved:
///   - The rebase base comes from the freshly fetched authoritative remote refs
///     (origin/&lt;default&gt; and the branch tracking ref), never local/stale state.
///   - On a force push it deliberately skips fetching the branch tracking ref so it
///     stays the last-observed head — the push step's force-with-lease anchor.
///   - A gated branch that bundles unpushed local-default commits parks for a human
///     decision (<see cref="DetectBundledLocalDefaultCommitsAsync"/>).
///
/// The agent conflict-resolution path drives <see cref="IAgent"/>.
/// </summary>
public sealed class RebaseStep : IStep
{
    private readonly GitClient git = new();

    public string Name => StepName.Rebase;

    public async Task<StepOutcome> ExecuteAsync(StepContext sctx)
    {
        var branch = StripHeads(sctx.Run.Branch);
        var defaultBranch = sctx.Repo.DefaultBranch.Trim();
        if (defaultBranch.Length == 0)
        {
            defaultBranch = "main";
        }
        var branchTarget = string.Empty;
        var pushRemote = "origin";
        var usingFork = sctx.Repo.ForkUrl.Trim().Length > 0;
        if (branch.Length > 0)
        {
            branchTarget = "origin/" + branch;
            if (usingFork)
            {
                pushRemote = sctx.Repo.PushUrl();
                branchTarget = StepHelpers.ForkBranchTrackingRef(branch);
            }
        }

        // Detect force push before fetching so we can skip pushed-branch sync.
        var forcePush = await IsForcePushAgainstRemoteAsync(sctx, pushRemote, branch, branchTarget, sctx.Run.BaseSha)
            .ConfigureAwait(false);

        sctx.Log("fetching latest upstream state...");
        await FetchTry(sctx, "origin", defaultBranch,
            $"warning: could not fetch origin/{defaultBranch}").ConfigureAwait(false);

        // Sync the push branch's tracking ref only on a normal push. On a force push
        // we skip both the fetch and the rebase: the tracking ref must keep pointing
        // at the head we last observed (the push step's lease anchor).
        if (!forcePush && branch.Length > 0 && branch != defaultBranch)
        {
            if (pushRemote == "origin")
            {
                await FetchTry(sctx, "origin", branch, $"warning: could not fetch origin/{branch}").ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await git.FetchRemoteBranchToRefAsync(sctx.WorkDir, pushRemote, branch, branchTarget, sctx.Ct)
                        .ConfigureAwait(false);
                }
                catch (GitCommandException ex)
                {
                    sctx.LogFile($"warning: could not fetch {branchTarget}: {ex.Message}");
                }
            }
        }

        var bundled = await DetectBundledLocalDefaultCommitsAsync(sctx, branch, defaultBranch).ConfigureAwait(false);
        if (bundled != null)
        {
            return bundled;
        }

        if (forcePush && branch == defaultBranch
            && await RemoteDefaultBranchAdvancedAsync(sctx, defaultBranch, sctx.Run.BaseSha).ConfigureAwait(false))
        {
            var findings = new Findings
            {
                Items =
                {
                    new Finding
                    {
                        Severity = "warning",
                        File = Path.Combine("internal", "pipeline", "steps", "rebase.go"),
                        Description = $"origin/{defaultBranch} advanced after the force push; manual review required before updating the default branch",
                    },
                },
                Summary = $"remote {defaultBranch} advanced during force push",
            };
            return new StepOutcome { NeedsApproval = true, Findings = FindingsOps.Marshal(findings) };
        }

        var targets = RebaseTargets(branch, defaultBranch, branchTarget);
        if (forcePush)
        {
            sctx.Log($"force push detected, skipping {branchTarget} sync");
            targets = ForcePushRebaseTargets(branch, defaultBranch);
        }

        if (sctx.Fixing)
        {
            foreach (var target in targets)
            {
                await RebaseWithAgentAsync(sctx, target).ConfigureAwait(false);
            }
            return await UpdateHeadShaAsync(sctx, defaultBranch).ConfigureAwait(false);
        }

        var conflictTargets = new List<string>();
        var conflictFindings = new List<Finding>();
        foreach (var target in targets)
        {
            var conflictFiles = await TryRebaseAsync(sctx, target).ConfigureAwait(false);
            if (conflictFiles.Count > 0)
            {
                conflictTargets.Add(target);
                foreach (var file in conflictFiles)
                {
                    conflictFindings.Add(new Finding
                    {
                        Severity = "warning",
                        File = file,
                        Description = $"merge conflict rebasing onto {target}",
                    });
                }
            }
        }

        if (conflictTargets.Count > 0)
        {
            var findings = new Findings
            {
                Items = DedupeRebaseFindings(conflictFindings),
                Summary = $"conflict rebasing onto {string.Join(", ", conflictTargets)}",
            };
            return new StepOutcome
            {
                NeedsApproval = true,
                AutoFixable = true,
                Findings = FindingsOps.Marshal(findings),
            };
        }

        return await UpdateHeadShaAsync(sctx, defaultBranch).ConfigureAwait(false);
    }

    private static List<string> RebaseTargets(string branch, string defaultBranch, string branchTarget)
    {
        var targets = new List<string>();
        if (branch.Length > 0 && branch != defaultBranch)
        {
            targets.Add(branchTarget);
        }
        if (branch != defaultBranch)
        {
            targets.Add("origin/" + defaultBranch);
        }
        return targets;
    }

    // Force-push rebase targets skip the pushed branch tracking ref (it may carry
    // prior-run autofix commits the force push intended to discard).
    private static List<string> ForcePushRebaseTargets(string branch, string defaultBranch) =>
        branch == defaultBranch ? new List<string>() : new List<string> { "origin/" + defaultBranch };

    /// <summary>
    /// Parks with a blocking ask-user finding when the gated branch carries commits
    /// that exist on the contributor's local default branch but were never pushed to
    /// origin/&lt;default&gt;. Best-effort: returns null (proceed) when no such
    /// divergence is detected or the working repo cannot be read. Mirrors Go's
    /// detectBundledLocalDefaultCommits.
    /// </summary>
    public async Task<StepOutcome?> DetectBundledLocalDefaultCommitsAsync(
        StepContext sctx, string branch, string defaultBranch)
    {
        if (branch.Length == 0 || branch == defaultBranch)
        {
            return null;
        }
        var workingPath = sctx.Repo.WorkingPath.Trim();
        if (workingPath.Length == 0)
        {
            return null;
        }
        string localTip;
        try
        {
            localTip = (await git.RunAsync(workingPath,
                new[] { "rev-parse", "--verify", "--quiet", "refs/heads/" + defaultBranch + "^{commit}" }, sctx.Ct)
                .ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException)
        {
            return null;
        }
        if (localTip.Length == 0)
        {
            return null;
        }
        var remoteRef = "origin/" + defaultBranch;
        if (!await GitVerifyAsync(sctx, remoteRef + "^{commit}").ConfigureAwait(false))
        {
            return null;
        }
        if (!await GitVerifyAsync(sctx, localTip + "^{commit}").ConfigureAwait(false))
        {
            return null;
        }
        // Already pushed (local default not ahead of remote) -> nothing bundled.
        if (await StepHelpers.IsAncestorAsync(sctx, localTip, remoteRef).ConfigureAwait(false))
        {
            return null;
        }
        // The branch must actually carry the local default tip's commits.
        if (!await StepHelpers.IsAncestorAsync(sctx, localTip, "HEAD").ConfigureAwait(false))
        {
            return null;
        }

        string subjects;
        try
        {
            subjects = await git.RunAsync(sctx.WorkDir,
                new[] { "log", "--oneline", "--no-decorate", remoteRef + ".." + localTip }, sctx.Ct)
                .ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            return null;
        }
        if (subjects.Trim().Length == 0)
        {
            return null;
        }
        var commits = subjects.Trim().Split('\n');
        IReadOnlyList<string> files;
        try
        {
            files = await git.DiffNameOnlyAsync(sctx.WorkDir, remoteRef, localTip, sctx.Ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            files = Array.Empty<string>();
        }
        var firstFile = files.Count > 0 ? files[0] : string.Empty;

        var description =
            $"branch carries {commits.Length} commit(s) that exist on your local {defaultBranch} branch but were "
          + $"never pushed to origin/{defaultBranch}; rebasing would bundle this unrelated work ({files.Count} file(s)) "
          + $"into the PR:\n- {string.Join("\n- ", commits)}\n\n"
          + $"Push {defaultBranch} to origin, or rebase your branch onto origin/{defaultBranch}, before gating.";
        var findings = new Findings
        {
            Items =
            {
                new Finding
                {
                    Severity = "warning",
                    File = firstFile,
                    Description = description,
                    Action = FindingActions.AskUser,
                },
            },
            Summary = $"branch bundles {commits.Length} unpushed {defaultBranch} commit(s)",
        };
        return new StepOutcome
        {
            NeedsApproval = true,
            AutoFixable = false,
            Findings = FindingsOps.Marshal(findings),
        };
    }

    private async Task<bool> RemoteDefaultBranchAdvancedAsync(StepContext sctx, string defaultBranch, string baseSha)
    {
        if (baseSha.Length == 0 || GitClient.IsZeroSha(baseSha))
        {
            return false;
        }
        try
        {
            var remoteSha = (await git.RunAsync(sctx.WorkDir,
                new[] { "rev-parse", "--verify", "origin/" + defaultBranch }, sctx.Ct).ConfigureAwait(false)).Trim();
            return remoteSha != baseSha;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    // isForcePushAgainstRemote: the push is non-fast-forward relative to the run
    // base (baseSha not an ancestor of HEAD) and the remote branch was rewritten.
    private async Task<bool> IsForcePushAgainstRemoteAsync(
        StepContext sctx, string remote, string branch, string localRef, string baseSha)
    {
        if (GitClient.IsZeroSha(baseSha) || baseSha.Length == 0)
        {
            return false;
        }
        if (await StepHelpers.IsAncestorAsync(sctx, baseSha, "HEAD").ConfigureAwait(false))
        {
            return false; // fast-forward: not a force push
        }
        if (branch.Length == 0)
        {
            return false;
        }
        string remoteSha;
        try
        {
            remoteSha = await git.LsRemoteAsync(sctx.WorkDir, remote, "refs/heads/" + branch, sctx.Ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            remoteSha = string.Empty;
        }
        if (remoteSha.Length > 0)
        {
            // Remote head is an ancestor of HEAD -> fast-forward, not a rewrite.
            if (await StepHelpers.IsAncestorAsync(sctx, remoteSha, "HEAD").ConfigureAwait(false))
            {
                return false;
            }
            return true;
        }
        if (localRef.Length > 0 && await GitVerifyAsync(sctx, localRef).ConfigureAwait(false))
        {
            // Local tracking ref exists but is not an ancestor of HEAD -> rewritten.
            return !await StepHelpers.IsAncestorAsync(sctx, localRef, "HEAD").ConfigureAwait(false);
        }
        return false;
    }

    private async Task<List<string>> TryRebaseAsync(StepContext sctx, string targetRef)
    {
        if (await ShouldSkipRebaseAsync(sctx, targetRef).ConfigureAwait(false))
        {
            return new List<string>();
        }
        sctx.Log($"rebasing onto {targetRef}...");
        try
        {
            await git.RunAsync(sctx.WorkDir, new[] { "rebase", targetRef }, sctx.Ct).ConfigureAwait(false);
            return new List<string>();
        }
        catch (GitCommandException ex)
        {
            var conflictFiles = await RebaseConflictFilesAsync(sctx).ConfigureAwait(false);
            await GitTry(sctx, "rebase", "--abort").ConfigureAwait(false);
            if (conflictFiles.Count == 0)
            {
                throw new InvalidOperationException($"rebase onto {targetRef}: {ex.Message}", ex);
            }
            return conflictFiles;
        }
    }

    private async Task RebaseWithAgentAsync(StepContext sctx, string targetRef)
    {
        if (await ShouldSkipRebaseAsync(sctx, targetRef).ConfigureAwait(false))
        {
            return;
        }
        sctx.Log($"rebasing onto {targetRef}...");
        try
        {
            await git.RunAsync(sctx.WorkDir, new[] { "rebase", targetRef }, sctx.Ct).ConfigureAwait(false);
            return;
        }
        catch (GitCommandException)
        {
            // fall through to conflict handling
        }

        var conflictFiles = await RebaseConflictFilesAsync(sctx).ConfigureAwait(false);
        if (conflictFiles.Count == 0)
        {
            await GitTry(sctx, "rebase", "--abort").ConfigureAwait(false);
            throw new InvalidOperationException($"rebase onto {targetRef} failed (no conflicts detected)");
        }
        sctx.Log("conflicts detected, asking agent to resolve...");

        var prompt = $"""
Resolve git rebase conflicts. The rebase of the current branch onto {targetRef} has conflicts.

Current conflicted files:
- {string.Join("\n- ", conflictFiles)}

Instructions:
- Find all conflicting files and resolve the conflict markers (<<<<<<< ======= >>>>>>>).
- After resolving each file, stage it with: git add <file>
- After all conflicts are resolved, run: git rebase --continue
- Do not modify any files that don't have conflicts.
- Preserve the intent of both the current branch changes and the upstream changes.
- Return JSON with a single "summary" field describing what you resolved.
- Keep the summary under 10 words.
""";
        if (sctx.PreviousFindings.Length > 0)
        {
            prompt += "\n\nPrevious findings:\n" + sctx.PreviousFindings;
        }

        try
        {
            await sctx.Agent!.RunAsync(new AgentRunOpts
            {
                Prompt = prompt,
                Cwd = sctx.WorkDir,
                JsonSchema = StepSchemas.CommitSummary,
                OnChunk = sctx.LogChunk,
            }, sctx.Ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await GitTry(sctx, "rebase", "--abort").ConfigureAwait(false);
            throw new InvalidOperationException($"agent resolve conflicts: {ex.Message}", ex);
        }

        if (await RebaseInProgressAsync(sctx).ConfigureAwait(false))
        {
            await GitTry(sctx, "rebase", "--abort").ConfigureAwait(false);
            throw new InvalidOperationException("agent did not complete the rebase");
        }
    }

    // Whether a rebase onto targetRef can be skipped (missing, already merged, or
    // fast-forwardable). Mirrors Go's shouldSkipRebase.
    private async Task<bool> ShouldSkipRebaseAsync(StepContext sctx, string targetRef)
    {
        if (!await GitVerifyAsync(sctx, targetRef).ConfigureAwait(false))
        {
            return true;
        }
        var localSha = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();
        var targetSha = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", targetRef }, sctx.Ct).ConfigureAwait(false)).Trim();
        if (localSha == targetSha)
        {
            sctx.Log($"already up-to-date with {targetRef}");
            return true;
        }
        if (await StepHelpers.IsAncestorAsync(sctx, targetRef, "HEAD").ConfigureAwait(false))
        {
            sctx.Log($"already ahead of {targetRef}");
            return true;
        }
        if (await StepHelpers.IsAncestorAsync(sctx, "HEAD", targetRef).ConfigureAwait(false))
        {
            sctx.Log($"fast-forwarding to {targetRef}");
            await git.RunAsync(sctx.WorkDir, new[] { "reset", "--hard", targetRef }, sctx.Ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task<bool> RebaseInProgressAsync(StepContext sctx)
    {
        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            string p;
            try
            {
                p = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "--git-path", dir }, sctx.Ct).ConfigureAwait(false)).Trim();
            }
            catch (GitCommandException)
            {
                continue;
            }
            if (!Path.IsPathRooted(p))
            {
                p = Path.Combine(sctx.WorkDir, p);
            }
            if (Directory.Exists(p) || File.Exists(p))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<List<string>> RebaseConflictFilesAsync(StepContext sctx)
    {
        var outText = await GitTry(sctx, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
        var files = new List<string>();
        foreach (var raw in outText.Trim().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                files.Add(line);
            }
        }
        return files;
    }

    private static List<Finding> DedupeRebaseFindings(List<Finding> findings)
    {
        if (findings.Count < 2)
        {
            return findings;
        }
        var seen = new HashSet<string>();
        var filtered = new List<Finding>();
        foreach (var f in findings)
        {
            var key = f.File + "\x00" + f.Description;
            if (seen.Add(key))
            {
                filtered.Add(f);
            }
        }
        return filtered;
    }

    // Syncs the run head after rebase and flags an empty branch diff (SkipRemaining).
    private async Task<StepOutcome> UpdateHeadShaAsync(StepContext sctx, string defaultBranch)
    {
        var headSha = (await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();
        if (headSha.Length > 0 && headSha != sctx.Run.HeadSha)
        {
            sctx.Run.HeadSha = headSha;
            sctx.Db.UpdateRunHeadSha(sctx.Run.Id, headSha);
            sctx.Log($"updated head SHA to {ForcePush.ShortSha(headSha)}");
        }

        var baseSha = await StepHelpers.ResolveBranchBaseShaAsync(sctx, sctx.Run.BaseSha, defaultBranch).ConfigureAwait(false);
        try
        {
            var diff = await git.DiffAsync(sctx.WorkDir, baseSha, "HEAD", sctx.Ct).ConfigureAwait(false);
            if (diff.Trim().Length == 0)
            {
                sctx.Log("empty diff after rebase, skipping remaining steps");
                return new StepOutcome { SkipRemaining = true };
            }
        }
        catch (GitCommandException)
        {
            // diff failed: proceed without the empty-diff shortcut.
        }
        return new StepOutcome();
    }

    private async Task FetchTry(StepContext sctx, string remote, string branch, string warning)
    {
        try
        {
            await git.FetchRemoteBranchAsync(sctx.WorkDir, remote, branch, sctx.Ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            sctx.LogFile($"{warning}: {ex.Message}");
        }
    }

    private async Task<bool> GitVerifyAsync(StepContext sctx, string reference)
    {
        try
        {
            await git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "--verify", "--quiet", reference }, sctx.Ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (GitCommandException)
        {
            return false;
        }
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

    private static string StripHeads(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
}
