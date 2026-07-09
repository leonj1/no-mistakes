using System.Text.Json;
using NoMistakes.Core;
using NoMistakes.Git;
using NoMistakes.Ipc;

namespace NoMistakes.Cli;

/// <summary>
/// The mutating axi drive surface: `axi run` (trigger a pipeline run for the
/// current branch and drive it to a decision point or outcome), `axi respond`
/// (answer the current approval gate and continue), and the worktree/branch-
/// scoped `axi abort`. Ports Go's internal/cli/axi_drive.go (runAxiRun,
/// driveRun, renderDriveResult, runAxiRespond, runAxiAbort).
/// </summary>
public static class AxiDrive
{
    /// <summary>
    /// How often the drive loop re-reads run state. Short enough to feel
    /// responsive to an agent, long enough to avoid hammering the daemon
    /// during long agent steps.
    /// </summary>
    internal static TimeSpan DrivePollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Bounds how long we wait for the daemon to register a run after pushing
    /// to the gate before falling back to a rerun.
    /// </summary>
    internal static TimeSpan TriggerWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan TriggerPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The canonical guidance an agent reads at the checks-passed decision
    /// point. Byte-for-byte copy of Go's staleMonitorGuidance.
    /// </summary>
    public const string StaleMonitorGuidance =
        "If this PR later falls behind the default branch or hits a merge conflict, the CI monitor rebases onto the base, resolves it, and re-pushes the branch automatically - run no command and never hand-rebase. Only when that monitor is no longer running (PR closed, run aborted, idle-timeout, or auto-fix exhausted) recover with `no-mistakes rerun`.";

    /// <summary>Maps a terminal run status onto an agent-facing outcome word.</summary>
    public static string OutcomeFor(string status) => status switch
    {
        RunStatus.Completed => "passed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => status,
    };

    /// <summary>
    /// The `axi run` operation over an already-opened daemon-connected env.
    /// Ports Go's runAxiRun. <paramref name="ciChecksPassed"/> reports whether
    /// the CI step's logs show all checks green for a run id; pass null until
    /// the CI monitor slice lands (no early checks-passed return).
    /// </summary>
    public static async Task<AxiOutput> RunAsync(
        AxiEnv env,
        TextWriter? progress,
        bool autoYes,
        IReadOnlyList<string> skipSteps,
        string intent,
        Func<string, bool>? ciChecksPassed = null,
        CancellationToken ct = default)
    {
        var client = env.Client ?? throw new InvalidOperationException("axi run requires a daemon connection");
        var git = new GitClient();

        string branch;
        try
        {
            branch = (await git.CurrentBranchAsync(".", ct).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException ex)
        {
            return AxiOutput.Error(1, $"get current branch: {ex.Message}");
        }
        if (branch == "HEAD")
        {
            return AxiOutput.Error(1, "detached HEAD: check out a branch before validating",
                "Run `git switch -c <branch>` to put your commits on a branch");
        }

        string headSha;
        try
        {
            headSha = (await git.RunAsync(".", new[] { "rev-parse", "HEAD" }, ct).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException ex)
        {
            return AxiOutput.Error(1, $"get current HEAD: {ex.Message}");
        }

        var runId = await ActiveRunIdAsync(client, env.Repo.Id, branch, headSha, ct).ConfigureAwait(false);
        if (runId.Length == 0)
        {
            // Intent is mandatory when starting a run: the agent driving this
            // knows the change's intent, so we take it directly instead of
            // inferring it from transcripts. Reattaching to an in-flight run
            // does not need it.
            if (intent.Trim().Length == 0)
            {
                return AxiOutput.Error(2, "--intent is required to start a run",
                    "Pass what the user set out to accomplish: no-mistakes axi run --intent \"the user's goal\"");
            }
            // Starting a fresh run: apply the same pre-flight the human wizard
            // enforces, but as structured errors the agent acts on rather than
            // silent auto-branching/auto-committing. The gate validates
            // committed history, so a wrong branch or uncommitted work would
            // otherwise be validated incorrectly or not at all.
            if (await PreflightGuardAsync(git, env, branch, ct).ConfigureAwait(false) is { } guard)
            {
                return guard;
            }
            try
            {
                runId = await TriggerRunAsync(git, client, env.Repo.Id, branch, headSha, skipSteps, intent, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return AxiOutput.Error(1, ex.Message);
            }
        }

        RunInfo run;
        bool ciReady;
        try
        {
            (run, ciReady) = await DriveRunAsync(client, progress, runId, autoYes, ciChecksPassed, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"drive run: {ex.Message}");
        }
        return RenderDriveResult(run, ciReady);
    }

    /// <summary>
    /// Returns the id of a non-terminal run for branch and head, or "" if none.
    /// </summary>
    internal static async Task<string> ActiveRunIdAsync(
        IpcClient client, string repoId, string branch, string headSha, CancellationToken ct)
    {
        GetActiveRunResult active;
        try
        {
            active = await client.CallAsync<GetActiveRunResult>(
                Methods.GetActiveRun, new GetActiveRunParams { RepoId = repoId, Branch = branch }, ct)
                .ConfigureAwait(false) ?? new GetActiveRunResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
        return ActiveRunInfoForHead(active.Run, headSha)?.Id ?? string.Empty;
    }

    internal static RunInfo? ActiveRunInfoForHead(RunInfo? run, string headSha)
    {
        if (run == null || AxiRender.TerminalStatus(run.Status) || run.HeadSha != headSha)
        {
            return null;
        }
        return run;
    }

    /// <summary>
    /// Returns the structured error for the first unmet pre-flight condition
    /// when starting a new run, or null when the branch is ready to validate.
    /// Mirrors the wizard's branch/commit hygiene as detect-and-guide: refuse
    /// the default branch, and refuse an uncommitted working tree, each with
    /// the command the agent should run.
    /// </summary>
    internal static async Task<AxiOutput?> PreflightGuardAsync(
        GitClient git, AxiEnv env, string branch, CancellationToken ct)
    {
        if (env.Repo.DefaultBranch.Length > 0 && branch == env.Repo.DefaultBranch)
        {
            return AxiOutput.Error(1, $"refusing to validate \"{branch}\": it is the default branch",
                "Put your changes on a feature branch: `git switch -c <branch>`, then re-run");
        }
        bool dirty;
        try
        {
            dirty = await git.HasUncommittedChangesAsync(".", ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            return AxiOutput.Error(1, $"inspect working tree: {ex.Message}",
                "Run `git status` to check the repository state, then re-run");
        }
        if (dirty)
        {
            return AxiOutput.Error(1, "uncommitted changes in the working tree",
                "Commit your work before validating: `git add -A && git commit -m \"...\"`, then re-run",
                "Run `git status` to see what is uncommitted");
        }
        return null;
    }

    /// <summary>
    /// Starts a fresh run for branch: pushes the current HEAD through the gate
    /// to trigger a pipeline, and falls back to a rerun when the push was a
    /// no-op (the gate already had this commit). Callers must check for an
    /// existing active run first and apply pre-flight guards. Ports Go's
    /// triggerRun.
    /// </summary>
    internal static async Task<string> TriggerRunAsync(
        GitClient git,
        IpcClient client,
        string repoId,
        string branch,
        string headSha,
        IReadOnlyList<string> skipSteps,
        string intent,
        CancellationToken ct)
    {
        var pushOptions = DaemonNotifyPush.FormatSkipPushOptions(skipSteps);
        var intentOption = DaemonNotifyPush.FormatIntentPushOption(intent);
        if (intentOption.Length > 0)
        {
            pushOptions.Add(intentOption);
        }

        Exception? pushErr = null;
        try
        {
            await git.PushWithOptionsAsync(".", Gate.RemoteName, "refs/heads/" + branch, "", false, pushOptions, ct)
                .ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            pushErr = ex;
        }

        var run = await WaitForActiveRunForHeadAsync(client, repoId, branch, headSha, TriggerWaitTimeout, ct)
            .ConfigureAwait(false);
        if (run != null)
        {
            return run.Id;
        }
        if (pushErr != null)
        {
            throw new InvalidOperationException($"push \"{branch}\" to gate: {pushErr.Message}");
        }

        // No run appeared: the push was likely up-to-date. Rerun the latest
        // gate head so `axi run` is still useful when there are no new commits.
        try
        {
            var rr = await client.CallAsync<RerunResult>(Methods.Rerun, new RerunParams
            {
                RepoId = repoId,
                Branch = branch,
                SkipSteps = skipSteps.Count > 0 ? skipSteps.ToList() : null,
                Intent = intent.Length > 0 ? intent : null,
            }, ct).ConfigureAwait(false);
            return rr?.RunId ?? string.Empty;
        }
        catch (IpcRpcException ex)
        {
            throw new InvalidOperationException($"no run started for \"{branch}\": {ex.Message}");
        }
    }

    internal static async Task<RunInfo?> WaitForActiveRunForHeadAsync(
        IpcClient client, string repoId, string branch, string headSha, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await client.CallAsync<GetActiveRunResult>(
                Methods.GetActiveRun, new GetActiveRunParams { RepoId = repoId, Branch = branch }, ct)
                .ConfigureAwait(false);
            if (ActiveRunInfoForHead(result?.Run, headSha) is { } run)
            {
                return run;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }
            await Task.Delay(TriggerPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls a run until it reaches an approval gate, a terminal state, or CI
    /// checks pass, streaming step transitions to progress (stderr). When
    /// autoApprove is set it resolves each gate and continues; otherwise it
    /// returns at the first gate so the caller can surface it for a
    /// human/agent decision. Ports Go's driveRun (see that comment for the
    /// full auto-resolution and CI-ready semantics).
    /// </summary>
    internal static async Task<(RunInfo Run, bool CiReady)> DriveRunAsync(
        IpcClient client,
        TextWriter? progress,
        string runId,
        bool autoApprove,
        Func<string, bool>? ciChecksPassed,
        CancellationToken ct)
    {
        var pp = new ProgressPrinter(progress);
        var fixedSteps = new HashSet<string>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var run = await GetRunInfoAsync(client, runId, ct).ConfigureAwait(false);
            if (run == null)
            {
                throw new InvalidOperationException($"run {runId} not found");
            }
            pp.Update(run);

            var rv = RunView.FromIpc(run);
            if (AxiRender.TerminalStatus(rv.Status))
            {
                return (run, false);
            }
            if (rv.AwaitingStep() is { } gate)
            {
                if (!autoApprove)
                {
                    return (run, false);
                }
                var (action, findingIds) = GateResolution(gate, fixedSteps.Contains(gate.Name));
                if (action == ApprovalAction.Fix)
                {
                    fixedSteps.Add(gate.Name);
                }
                try
                {
                    await SendRespondAsync(client, runId, gate.Name, action, findingIds, null, null, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"auto-resolve {gate.Name}: {ex.Message}");
                }
                await WaitStepLeavesGateAsync(client, runId, gate.Name, gate.Status, ct).ConfigureAwait(false);
                continue;
            }
            // CI is green but the PR is unmerged: hand control back rather
            // than waiting on a human merge. This holds even under
            // autoApprove, since the agent cannot approve away a human's merge.
            if (ciChecksPassed != null && CiMonitoring(rv) && ciChecksPassed(runId))
            {
                return (run, true);
            }
            await Task.Delay(DrivePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the CI step is actively monitoring (the log-side "all checks
    /// passed" half of Go's ciReadyToMerge lives behind the ciChecksPassed
    /// callback until the CI monitor slice lands).
    /// </summary>
    private static bool CiMonitoring(RunView rv)
    {
        foreach (var s in rv.Steps)
        {
            if (s.Name == StepName.Ci)
            {
                return s.Status == StepStatus.Running;
            }
        }
        return false;
    }

    /// <summary>
    /// Decides how --yes answers an approval gate. A gate with actionable
    /// findings is fixed with every finding selected, unless this step was
    /// already fixed once - then it is approved so the run converges instead
    /// of looping on a finding the fix cannot clear. Gates with only
    /// non-actionable findings, no findings, or actionable findings that carry
    /// no IDs are approved. Ports Go's gateResolution.
    /// </summary>
    internal static (string Action, List<string> FindingIds) GateResolution(StepView gate, bool alreadyFixed)
    {
        if (alreadyFixed || gate.Status == StepStatus.FixReview)
        {
            return (ApprovalAction.Approve, new List<string>());
        }
        var parsed = AxiRender.ParseFindingsOrEmpty(gate.FindingsJson);
        if (!parsed.HasActionable())
        {
            return (ApprovalAction.Approve, new List<string>());
        }
        var ids = new List<string>();
        foreach (var f in parsed.Items)
        {
            if (f.Id.Length > 0)
            {
                ids.Add(f.Id);
            }
        }
        if (ids.Count == 0)
        {
            return (ApprovalAction.Approve, new List<string>());
        }
        return (ApprovalAction.Fix, ids);
    }

    /// <summary>
    /// Blocks until the named step's status changes away from the gate status
    /// we just answered, or the run terminates. Prevents a double-approve
    /// race: respond is asynchronous, so without waiting the next poll could
    /// still observe the same gate and approve it twice.
    /// </summary>
    internal static async Task WaitStepLeavesGateAsync(
        IpcClient client, string runId, string step, string gateStatus, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var run = await GetRunInfoAsync(client, runId, ct).ConfigureAwait(false);
            if (run == null || AxiRender.TerminalStatus(run.Status))
            {
                return;
            }
            var current = run.Steps?.FirstOrDefault(s => s.StepName == step);
            if (current != null && current.Status != gateStatus)
            {
                return;
            }
            await Task.Delay(DrivePollInterval, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<RunInfo?> GetRunInfoAsync(IpcClient client, string runId, CancellationToken ct)
    {
        var result = await client.CallAsync<GetRunResult>(
            Methods.GetRun, new GetRunParams { RunId = runId }, ct).ConfigureAwait(false);
        return result?.Run;
    }

    /// <summary>Issues an approval action to the daemon for a step.</summary>
    internal static async Task SendRespondAsync(
        IpcClient client,
        string runId,
        string step,
        string action,
        List<string>? findingIds,
        Dictionary<string, string>? instructions,
        List<Finding>? added,
        CancellationToken ct)
    {
        var result = await client.CallAsync<RespondResult>(Methods.Respond, new RespondParams
        {
            RunId = runId,
            Step = step,
            Action = action,
            FindingIds = findingIds is { Count: > 0 } ? findingIds : null,
            Instructions = instructions,
            AddedFindings = added,
        }, ct).ConfigureAwait(false);
        if (result is not { Ok: true })
        {
            throw new InvalidOperationException("daemon rejected the response");
        }
    }

    /// <summary>
    /// Renders the run snapshot plus one of: the active gate (exit 0, a normal
    /// decision point), a checks-passed outcome (exit 0, CI is green and the
    /// PR is ready for a human to merge), or the terminal outcome (exit 0 when
    /// passed, exit 1 when failed or cancelled). Successful outcomes also
    /// carry the fixes the pipeline applied and reporting instructions, so the
    /// agent closes the loop with the user instead of stopping at "it passed".
    /// Ports Go's renderDriveResult.
    /// </summary>
    internal static AxiOutput RenderDriveResult(RunInfo run, bool ciReady)
    {
        var rv = RunView.FromIpc(run);
        var fields = new List<ToonField> { AxiRender.RunObjectField(rv) };

        // CI passed but the run is intentionally still monitoring for a human
        // merge. Report it as a distinct, successful outcome so the agent
        // stops and asks the user to review and merge instead of waiting.
        if (ciReady)
        {
            fields.Add(new ToonField("outcome", "checks-passed"));
            var merge = "CI checks passed - the PR is ready. Ask the user to review and merge it.";
            if (rv.PrUrl.Length > 0)
            {
                merge = $"CI checks passed - the PR is ready. Ask the user to review and merge it: {rv.PrUrl}";
            }
            var fixes = rv.FixRows();
            AppendFixesField(fields, fixes);
            var help = new List<string> { merge };
            help.AddRange(SuccessReportHelp(fixes));
            help.Add(StaleMonitorGuidance);
            fields.Add(new ToonField("help", help));
            return new AxiOutput(AxiRender.Doc(fields.ToArray()), 0);
        }

        if (rv.AwaitingStep() is { } gate)
        {
            fields.AddRange(AxiRender.GateFields(gate));
            return new AxiOutput(AxiRender.Doc(fields.ToArray()), 0);
        }

        fields.Add(new ToonField("outcome", OutcomeFor(rv.Status)));
        if (!string.IsNullOrEmpty(run.Error))
        {
            fields.Add(new ToonField("error", run.Error));
        }

        if (rv.Status == RunStatus.Completed)
        {
            var fixes = rv.FixRows();
            AppendFixesField(fields, fixes);
            var help = new List<string>();
            if (rv.PrUrl.Length > 0)
            {
                help.Add($"Open the PR: {rv.PrUrl}");
            }
            help.AddRange(SuccessReportHelp(fixes));
            fields.Add(new ToonField("help", help));
            return new AxiOutput(AxiRender.Doc(fields.ToArray()), 0);
        }

        if (rv.PrUrl.Length > 0)
        {
            fields.Add(new ToonField("help", new List<string> { $"Open the PR: {rv.PrUrl}" }));
        }
        return new AxiOutput(AxiRender.Doc(fields.ToArray()), 1);
    }

    /// <summary>Adds a fixes table when the pipeline applied any fixes.</summary>
    private static void AppendFixesField(List<ToonField> fields, List<ToonObject> fixes)
    {
        if (fixes.Count > 0)
        {
            fields.Add(new ToonField("fixes", fixes));
        }
    }

    /// <summary>
    /// The reporting instructions for a successful outcome: always summarize
    /// the run for the user, and when the pipeline applied fixes, own the
    /// misses and list every fix for the user's review.
    /// </summary>
    internal static List<string> SuccessReportHelp(List<ToonObject> fixes)
    {
        var help = new List<string>
        {
            "Summarize this pipeline run for the user in a concise, easily readable format: what was validated and what was found.",
        };
        if (fixes.Count > 0)
        {
            help.Add("The pipeline fixed findings the original change missed (see `fixes`) - acknowledge the misses and list each fix so the user can review them.");
        }
        return help;
    }

    /// <summary>
    /// Validates the `axi respond` --action value, returning the structured
    /// usage error (exit 2) for a missing or unknown action, or null when the
    /// trimmed action is one of approve/fix/skip. Runs before any environment
    /// is opened (Go validates the action first in runAxiRespond).
    /// </summary>
    public static AxiOutput? ValidateRespondAction(string action)
    {
        switch (action.Trim())
        {
            case ApprovalAction.Approve:
            case ApprovalAction.Fix:
            case ApprovalAction.Skip:
                return null;
            case "":
                return AxiOutput.Error(2, "--action is required",
                    "Run `no-mistakes axi respond --action approve|fix|skip`");
            default:
                return AxiOutput.Error(2, $"unknown action \"{action}\"",
                    "Valid actions: approve, fix, skip");
        }
    }

    /// <summary>
    /// Splits a comma-separated value into trimmed, non-empty parts. Ports
    /// Go's splitCSV (nil for no parts becomes an empty list).
    /// </summary>
    internal static List<string> SplitCsv(string s)
    {
        var result = new List<string>();
        foreach (var part in s.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }
        return result;
    }

    private static readonly JsonSerializerOptions AddFindingOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Decodes a user-authored finding from a JSON object string, throwing
    /// <see cref="FormatException"/> on malformed JSON or a missing
    /// description. Ports Go's parseAddFinding; the wire keys are Finding's
    /// JsonPropertyName tags (Go's json tags), matched case-insensitively
    /// like encoding/json.
    /// </summary>
    internal static Finding ParseAddFinding(string raw)
    {
        Finding? f;
        try
        {
            f = JsonSerializer.Deserialize<Finding>(raw, AddFindingOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message);
        }
        if (f == null || f.Description.Trim().Length == 0)
        {
            throw new FormatException("description is required");
        }
        return f;
    }

    /// <summary>
    /// The `axi respond` operation over an already-opened daemon-connected
    /// env: sends the approve/fix/skip action for the step awaiting approval
    /// (or the --step override; the daemon-side executor validates that the
    /// named step is actually parked) - for fix, carrying the selected
    /// finding IDs, the per-finding instructions note, and any added finding
    /// - then blocks until the next gate, CI-ready decision point, or final
    /// outcome, auto-resolving subsequent gates when autoYes is set. Ports
    /// Go's runAxiRespond; callers validate the action with
    /// <see cref="ValidateRespondAction"/> first.
    /// </summary>
    public static async Task<AxiOutput> RespondAsync(
        AxiEnv env,
        TextWriter? progress,
        string action,
        string step = "",
        string findings = "",
        string instructions = "",
        string addFinding = "",
        bool autoYes = false,
        Func<string, bool>? ciChecksPassed = null,
        CancellationToken ct = default)
    {
        var client = env.Client ?? throw new InvalidOperationException("axi respond requires a daemon connection");
        var act = action.Trim();

        string branch;
        try
        {
            branch = (await new GitClient().CurrentBranchAsync(".", ct).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException ex)
        {
            return AxiOutput.Error(1, $"get current branch: {ex.Message}");
        }

        GetActiveRunResult active;
        try
        {
            active = await client.CallAsync<GetActiveRunResult>(
                Methods.GetActiveRun, new GetActiveRunParams { RepoId = env.Repo.Id, Branch = branch }, ct)
                .ConfigureAwait(false) ?? new GetActiveRunResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"get active run: {ex.Message}");
        }
        if (active.Run == null)
        {
            return AxiOutput.Error(1, "no active run to respond to",
                "Run `no-mistakes axi run` to start one");
        }
        var runId = active.Run.Id;

        RunInfo? run;
        try
        {
            run = await GetRunInfoAsync(client, runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"load run: {ex.Message}");
        }
        if (run == null)
        {
            return AxiOutput.Error(1, $"load run: run {runId} not found");
        }
        var rv = RunView.FromIpc(run);

        // Go resolves the awaiting step (and its no-gate error) only when
        // --step is empty; an explicit --step skips the lookup and lets the
        // daemon-side executor validate that the step is actually parked.
        var stepName = step.Trim();
        if (stepName.Length == 0)
        {
            if (rv.AwaitingStep() is not { } gate)
            {
                return AxiOutput.Error(1, "no step is awaiting approval",
                    "Run `no-mistakes axi status` to see the run state");
            }
            stepName = gate.Name;
        }

        // Go computes the finding IDs for every action; instructions and the
        // added finding apply only to fix.
        var findingIds = SplitCsv(findings);
        Dictionary<string, string>? instructionsMap = null;
        List<Finding>? added = null;

        if (act == ApprovalAction.Fix)
        {
            if (findingIds.Count == 0 && addFinding.Length == 0)
            {
                return AxiOutput.Error(2, "--action fix requires --findings <id,...> or --add-finding <json>",
                    "Run `no-mistakes axi status` to list finding IDs");
            }
            var note = instructions.Trim();
            if (note.Length > 0 && findingIds.Count > 0)
            {
                instructionsMap = new Dictionary<string, string>(findingIds.Count);
                foreach (var id in findingIds)
                {
                    instructionsMap[id] = note;
                }
            }
            if (addFinding.Length > 0)
            {
                Finding f;
                try
                {
                    f = ParseAddFinding(addFinding);
                }
                catch (FormatException ex)
                {
                    return AxiOutput.Error(2, $"invalid --add-finding: {ex.Message}",
                        """Expected a JSON object, e.g. {"description":"...","action":"auto-fix"}""");
                }
                added = new List<Finding> { f };
            }
        }

        try
        {
            await SendRespondAsync(client, runId, stepName, act, findingIds, instructionsMap, added, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"respond to {stepName}: {ex.Message}");
        }

        // Let the executor consume the response before we re-read state, so
        // we don't immediately observe the same gate we just answered.
        try
        {
            await WaitStepLeavesGateAsync(client, runId, stepName, GateStatusFor(rv, stepName), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"wait for {stepName}: {ex.Message}");
        }

        RunInfo final;
        bool ciReady;
        try
        {
            (final, ciReady) = await DriveRunAsync(client, progress, runId, autoApprove: autoYes, ciChecksPassed, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"drive run: {ex.Message}");
        }
        return RenderDriveResult(final, ciReady);
    }

    /// <summary>
    /// The current status of a step in the run view, defaulting to the
    /// awaiting-approval status so the post-respond wait still functions if
    /// the step was not found. Ports Go's gateStatusFor.
    /// </summary>
    internal static string GateStatusFor(RunView rv, string step)
    {
        foreach (var s in rv.Steps)
        {
            if (s.Name == step)
            {
                return s.Status;
            }
        }
        return StepStatus.AwaitingApproval;
    }

    /// <summary>
    /// The worktree/branch-scoped `axi abort`: cancels the active run on the
    /// current branch, reporting an idempotent no-op when there is none. Ports
    /// the no-flag half of Go's runAxiAbort (abort-by-id is
    /// <see cref="AxiAbort.AbortByRunIdAsync"/>).
    /// </summary>
    public static async Task<AxiOutput> AbortAsync(AxiEnv env, CancellationToken ct = default)
    {
        var client = env.Client ?? throw new InvalidOperationException("axi abort requires a daemon connection");

        string branch;
        try
        {
            branch = (await new GitClient().CurrentBranchAsync(".", ct).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException ex)
        {
            return AxiOutput.Error(1, $"get current branch: {ex.Message}");
        }

        GetActiveRunResult active;
        try
        {
            active = await client.CallAsync<GetActiveRunResult>(
                Methods.GetActiveRun, new GetActiveRunParams { RepoId = env.Repo.Id, Branch = branch }, ct)
                .ConfigureAwait(false) ?? new GetActiveRunResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"get active run: {ex.Message}");
        }

        if (active.Run == null)
        {
            // Idempotent: nothing to abort is a successful no-op.
            return AxiOutput.Ok(
                new ToonField("aborted", false),
                new ToonField("detail", "no active run (no-op)"));
        }

        try
        {
            await client.CallAsync<CancelRunResult>(
                Methods.CancelRun, new CancelRunParams { RunId = active.Run.Id }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AxiOutput.Error(1, $"abort run: {ex.Message}");
        }
        return AxiOutput.Ok(
            new ToonField("aborted", true),
            new ToonField("run", active.Run.Id),
            new ToonField("branch", active.Run.Branch));
    }

    /// <summary>Renders an abort-by-id outcome. Ports the emitDoc calls in Go's runAxiAbortByRunID.</summary>
    public static AxiOutput RenderAbortByIdOutcome(AxiAbortOutcome outcome)
    {
        var fields = new List<ToonField>
        {
            new("aborted", outcome.Aborted),
            new("run", outcome.RunId),
        };
        if (outcome.Detail != null)
        {
            fields.Add(new ToonField("detail", outcome.Detail));
        }
        return AxiOutput.Ok(fields.ToArray());
    }

    /// <summary>
    /// Emits step and run status transitions to stderr so a human or agent
    /// watching the command sees liveness without parsing stdout. Ports Go's
    /// progressPrinter.
    /// </summary>
    internal sealed class ProgressPrinter
    {
        private readonly TextWriter? w;
        private readonly Dictionary<string, string> seen = new();
        private string runStatus = string.Empty;

        public ProgressPrinter(TextWriter? w)
        {
            this.w = w;
        }

        public void Update(RunInfo run)
        {
            if (w == null)
            {
                return;
            }
            if (run.Status != runStatus)
            {
                runStatus = run.Status;
                w.WriteLine($"run: {runStatus}");
            }
            foreach (var s in run.Steps ?? [])
            {
                if (s.Status == StepStatus.Pending)
                {
                    continue;
                }
                if (!seen.TryGetValue(s.StepName, out var prev) || prev != s.Status)
                {
                    seen[s.StepName] = s.Status;
                    w.WriteLine($"  {s.StepName}: {s.Status}");
                }
            }
        }
    }
}
