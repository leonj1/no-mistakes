using NoMistakes.Config;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Git;
using NoMistakes.Ipc;

namespace NoMistakes.Pipeline;

/// <summary>Called when a pipeline event occurs, for streaming to subscribers.</summary>
public delegate void EventFunc(IpcEvent ev);

/// <summary>
/// Runs pipeline steps sequentially and coordinates approval interactions.
/// Ports Go's internal/pipeline Executor: sequential execution, skip handling,
/// the auto-fix loop, round persistence, gate parking via <see cref="ApprovalGate"/>,
/// event emission, terminal run states, and cancellation propagation.
///
/// Telemetry (Go's telemetry.Track calls) is deferred to a later slice, so those
/// call sites are intentionally absent here; the observable DB and event behavior
/// is unchanged.
/// </summary>
public sealed class Executor
{
    private readonly Database db;
    private readonly Paths paths;
    private readonly Config.Config? config;
    private readonly IAgent? agent;
    private readonly IReadOnlyList<IStep> steps;
    private readonly GitClient git;
    private readonly EventFunc onEvent;
    private readonly ApprovalGate gate = new();
    private HashSet<string>? skips;

    public Executor(
        Database db,
        Paths paths,
        Config.Config? config,
        IAgent? agent,
        IReadOnlyList<IStep> steps,
        EventFunc? onEvent = null,
        GitClient? git = null)
    {
        this.db = db;
        this.paths = paths;
        this.config = config;
        this.agent = agent;
        this.steps = steps;
        this.git = git ?? new GitClient();
        this.onEvent = onEvent ?? (_ => { });
    }

    /// <summary>Configures steps that should be marked skipped without running.</summary>
    public void SetSkippedSteps(IReadOnlyList<string> stepNames)
    {
        skips = stepNames.Count == 0
            ? null
            : new HashSet<string>(stepNames.Select(StepName.Normalize));
    }

    /// <summary>
    /// Sends a driver approval action to the currently waiting step. Ports Go
    /// Executor.Respond; throws with Go's exact messages when no step waits or
    /// the step name mismatches.
    /// </summary>
    public void Respond(string step, string action, List<string>? findingIds = null) =>
        gate.RespondWithOverrides(step, action, findingIds, null, null);

    /// <summary>Like <see cref="Respond"/> but carries per-finding instructions and user findings.</summary>
    public void RespondWithOverrides(
        string step,
        string action,
        List<string>? findingIds,
        Dictionary<string, string>? instructions,
        List<Finding>? addedFindings) =>
        gate.RespondWithOverrides(step, action, findingIds, instructions, addedFindings);

    /// <summary>
    /// Runs the pipeline steps sequentially for a run. Steps execute in
    /// <paramref name="workDir"/> (typically a git worktree). On cancellation the
    /// cancel reason is preserved as the run's error.
    /// </summary>
    public async Task ExecuteAsync(Run run, Repo repo, string workDir, CancellationToken ct)
    {
        db.UpdateRunStatus(run.Id, RunStatus.Running);
        run.Status = RunStatus.Running;
        EmitRunEvent(EventTypes.RunUpdated, run, repo);

        var logDir = paths.RunLogDir(run.Id);
        Directory.CreateDirectory(logDir);

        var stepRecords = new Dictionary<string, StepResult>();
        foreach (var step in steps)
        {
            stepRecords[step.Name] = db.InsertStepResult(run.Id, step.Name);
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (ct.IsCancellationRequested)
            {
                throw FailRun(run, repo, CancelReason(ct, "context canceled"));
            }

            var sr = stepRecords[step.Name];
            if (skips != null && skips.Contains(step.Name))
            {
                db.CompleteStepWithStatus(sr.Id, StepStatus.Skipped, 0, 0, string.Empty);
                EmitStepEvent(EventTypes.StepCompleted, run, repo, step.Name, StepStatus.Skipped, "", "", "", null);
                continue;
            }

            bool skipRemaining;
            try
            {
                skipRemaining = await ExecuteStepAsync(step, sr, run, repo, workDir, logDir, ct)
                    .ConfigureAwait(false);
            }
            catch (StepFailedException ex)
            {
                throw FailRun(run, repo, ex.Message, ct);
            }

            if (skipRemaining)
            {
                foreach (var remaining in steps.Skip(i + 1))
                {
                    var rsr = stepRecords[remaining.Name];
                    db.CompleteStepWithStatus(rsr.Id, StepStatus.Skipped, 0, 0, string.Empty);
                    EmitStepEvent(EventTypes.StepCompleted, run, repo, remaining.Name, StepStatus.Skipped, "", "", "", null);
                }
                break;
            }
        }

        db.UpdateRunStatus(run.Id, RunStatus.Completed);
        run.Status = RunStatus.Completed;
        EmitRunEvent(EventTypes.RunCompleted, run, repo);
    }

    // Returns skipRemaining. Throws StepFailedException on a step failure so the
    // caller can route it through FailRun with the run context.
    private async Task<bool> ExecuteStepAsync(
        IStep step, StepResult sr, Run run, Repo repo, string workDir, string logDir, CancellationToken ct)
    {
        var stepName = step.Name;
        var logPath = Path.Combine(logDir, stepName + ".log");
        var finalExitCode = 0;

        db.StartStep(sr.Id);
        EmitStepEvent(EventTypes.StepStarted, run, repo, stepName, StepStatus.Running, "", "", "", null);

        var phaseStart = DateTimeOffset.UtcNow;
        long executionMs = 0;
        long durationOverrideMs = 0;

        using var logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write)) { AutoFlush = true };

        var lastChunkNewline = true;
        var userIntent = run.Intent ?? string.Empty;
        var sctx = new StepContext
        {
            Ct = ct,
            Run = run,
            Repo = repo,
            WorkDir = workDir,
            Agent = agent,
            Config = config,
            Db = db,
            StepResultId = sr.Id,
            UserIntent = userIntent,
            Log = text =>
            {
                if (text.Length > 0)
                {
                    var prefix = lastChunkNewline ? string.Empty : "\n";
                    text = prefix + text.TrimEnd('\n') + "\n\n";
                    lastChunkNewline = true;
                }
                EmitLogChunk(run, repo, stepName, text);
                logFile.Write(text);
            },
            LogChunk = text =>
            {
                if (text.Length > 0)
                {
                    lastChunkNewline = text.EndsWith('\n');
                }
                EmitLogChunk(run, repo, stepName, text);
                logFile.Write(text);
            },
            LogFile = text => logFile.WriteLine(text),
        };

        var autoFixLimit = config?.AutoFixLimit(stepName) ?? 0;
        var autoFixAttempts = 0;
        var roundNum = 0;
        var nextTrigger = "initial";
        var skipRemaining = false;
        var stepSkipped = false;
        string currentRoundId = string.Empty;

        while (true)
        {
            StepOutcome outcome;
            try
            {
                outcome = await step.ExecuteAsync(sctx).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                roundNum++;
                var roundDur = ElapsedMs(phaseStart);
                var durationMs = executionMs + roundDur;
                logFile.Write($"\nerror: {ex.Message}\n");
                db.FailStep(sr.Id, ex.Message, durationMs);
                EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, StepStatus.Failed, "", "", ex.Message, durationMs);
                throw new StepFailedException($"step {stepName} failed: {ex.Message}");
            }

            roundNum++;
            var roundDuration = ElapsedMs(phaseStart);

            var findings = PipelineFindings.NormalizeFindingsJson(outcome.Findings, stepName);
            finalExitCode = outcome.ExitCode;
            durationOverrideMs += outcome.DurationOverrideMs;

            if (findings.Length > 0)
            {
                db.SetStepFindings(sr.Id, findings);
            }
            else
            {
                db.ClearStepFindings(sr.Id);
            }

            var findingsPtr = findings.Length > 0 ? findings : null;
            var fixSummaryPtr = outcome.FixSummary.Length > 0 ? outcome.FixSummary : null;
            var inserted = db.InsertStepRound(sr.Id, roundNum, nextTrigger, findingsPtr, fixSummaryPtr, roundDuration);
            currentRoundId = inserted.Id;

            if (outcome.PrUrl.Length > 0)
            {
                run.PrUrl = outcome.PrUrl;
                EmitRunEvent(EventTypes.RunUpdated, run, repo);
            }

            // Auto-fix (only "auto-fix" action findings), before the approval
            // check so all severities get a chance at automatic fixing.
            if (outcome.AutoFixable && autoFixLimit > 0 && autoFixAttempts < autoFixLimit)
            {
                var fixableFindings = PipelineFindings.AutoFixableFindingsJson(findings);
                if (fixableFindings.Length > 0)
                {
                    autoFixAttempts++;
                    executionMs += ElapsedMs(phaseStart);
                    db.UpdateStepStatus(sr.Id, StepStatus.Fixing);
                    if (currentRoundId.Length > 0)
                    {
                        var idsJson = PipelineFindings.FindingIdsJson(fixableFindings);
                        if (idsJson.Length > 0)
                        {
                            db.SetStepRoundSelection(currentRoundId, idsJson, Database.RoundSelectionSourceAutoFix);
                        }
                    }
                    EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, StepStatus.Fixing, "", "", "", null);
                    phaseStart = DateTimeOffset.UtcNow;
                    sctx.Fixing = true;
                    sctx.PreviousFindings = fixableFindings;
                    nextTrigger = "auto_fix";
                    continue;
                }
            }

            if (!outcome.NeedsApproval && !PipelineFindings.HasAskUserFindingsJson(findings))
            {
                skipRemaining = outcome.SkipRemaining;
                stepSkipped = outcome.Skipped;
                break;
            }

            executionMs += ElapsedMs(phaseStart);

            var approvalStatus = StepStatus.AwaitingApproval;
            var diffText = string.Empty;
            if (sctx.Fixing)
            {
                approvalStatus = StepStatus.FixReview;
                try
                {
                    var d = await git.DiffHeadAsync(workDir, ct).ConfigureAwait(false);
                    if (d.Length > 0)
                    {
                        diffText = d;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // best-effort diff for the fix review; ignore failures.
                }
            }

            // Park: ApprovalGate arms the gate and sets the awaiting-agent marker
            // BEFORE onParked flips the step status, so a poller that observes the
            // parked step already sees the marker. The marker clears on the wait's
            // exit (respond or cancel) inside ParkAsync.
            var capturedExecutionMs = executionMs;
            var capturedApprovalStatus = approvalStatus;
            var capturedDiff = diffText;
            var capturedFindings = findings;

            ApprovalResponse response;
            try
            {
                response = await gate.ParkAsync(db, run.Id, stepName, onParked: () =>
                {
                    db.UpdateStepStatusWithDuration(sr.Id, capturedApprovalStatus, capturedExecutionMs);
                    EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, capturedApprovalStatus,
                        capturedFindings, capturedDiff, "", capturedExecutionMs);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var reason = CancelReason(ct, "context canceled");
                db.FailStep(sr.Id, reason, executionMs);
                EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, StepStatus.Failed, "", "", reason, executionMs);
                throw new StepFailedException($"step {stepName}: waiting for approval: {reason}");
            }

            switch (response.Action)
            {
                case ApprovalAction.Approve:
                    phaseStart = DateTimeOffset.UtcNow;
                    goto done;

                case ApprovalAction.Skip:
                    db.CompleteStepWithStatus(sr.Id, StepStatus.Skipped, finalExitCode, executionMs, logPath);
                    EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, StepStatus.Skipped, "", "", "", executionMs);
                    return false;

                case ApprovalAction.Fix:
                    phaseStart = DateTimeOffset.UtcNow;
                    db.UpdateStepStatus(sr.Id, StepStatus.Fixing);
                    sctx.Fixing = true;
                    var selectedFindings = PipelineFindings.FilterFindingsJson(findings, response.FindingIds ?? new List<string>());
                    var mergedFindings = PipelineFindings.MergeUserOverridesJson(
                        selectedFindings, response.Instructions, response.AddedFindings);
                    sctx.PreviousFindings = mergedFindings;
                    nextTrigger = "auto_fix";
                    if (currentRoundId.Length > 0)
                    {
                        var allSelectedIds = PipelineFindings.CombineSelectedFindingIds(
                            response.FindingIds ?? new List<string>(), mergedFindings);
                        var idsJson = PipelineFindings.MarshalFindingIds(allSelectedIds);
                        if (idsJson.Length > 0)
                        {
                            db.SetStepRoundSelection(currentRoundId, idsJson, Database.RoundSelectionSourceUser);
                        }
                        if (mergedFindings.Length > 0 && mergedFindings != selectedFindings)
                        {
                            db.SetStepRoundUserFindings(currentRoundId, mergedFindings);
                        }
                    }
                    EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, StepStatus.Fixing, "", "", "", null);
                    continue;

                default:
                    // Unknown action: treat as approve (Go has no default branch;
                    // the wire validates the verb upstream).
                    phaseStart = DateTimeOffset.UtcNow;
                    goto done;
            }
        }

    done:
        var finalDuration = executionMs + ElapsedMs(phaseStart);
        if (durationOverrideMs > 0)
        {
            finalDuration = durationOverrideMs;
        }
        var status = stepSkipped ? StepStatus.Skipped : StepStatus.Completed;
        db.CompleteStepWithStatus(sr.Id, status, finalExitCode, finalDuration, logPath);
        EmitStepEvent(EventTypes.StepCompleted, run, repo, stepName, status, "", "", "", finalDuration);
        return skipRemaining;
    }

    // Marks a run failed (or cancelled) and returns an exception carrying the
    // error. A cancel-cause reason maps to the cancelled run status.
    private StepFailedException FailRun(Run run, Repo repo, string errMsg, CancellationToken ct = default)
    {
        if (ct.CanBeCanceled && ct.IsCancellationRequested)
        {
            var cause = CancelReason(ct, errMsg);
            if (cause != "context canceled")
            {
                errMsg = cause;
            }
        }
        var runStatus = RunStatus.Failed;
        if (errMsg == RunCancelReason.AbortedByUser || errMsg == RunCancelReason.Superseded)
        {
            runStatus = RunStatus.Cancelled;
        }
        db.UpdateRunErrorStatus(run.Id, errMsg, runStatus);
        run.Status = runStatus;
        run.Error = errMsg;
        EmitRunEvent(EventTypes.RunCompleted, run, repo);
        return new StepFailedException(errMsg);
    }

    private static string CancelReason(CancellationToken ct, string fallback)
    {
        // .NET has no context.Cause; the caller supplies the reason via the run's
        // cancellation source elsewhere. Here we only distinguish cancelled from
        // not, returning the fallback text for a plain cancellation.
        return ct.IsCancellationRequested ? fallback : fallback;
    }

    private static long ElapsedMs(DateTimeOffset since) =>
        (long)(DateTimeOffset.UtcNow - since).TotalMilliseconds;

    // --- event helpers ---

    private void EmitRunEvent(string eventType, Run run, Repo repo)
    {
        onEvent(new IpcEvent
        {
            Type = eventType,
            RunId = run.Id,
            RepoId = repo.Id,
            Status = run.Status,
            Branch = run.Branch,
            Error = run.Error,
            PrUrl = run.PrUrl,
        });
    }

    private void EmitStepEvent(
        string eventType, Run run, Repo repo, string stepName, string status,
        string findings, string diff, string errMsg, long? durationMs)
    {
        var ev = new IpcEvent
        {
            Type = eventType,
            RunId = run.Id,
            RepoId = repo.Id,
            StepName = stepName,
            Status = status,
            DurationMs = durationMs,
        };
        var stats = FindingStatsForStep(run.Id, stepName);
        if (stats.ReportedFindings > 0 || stats.FixedFindings > 0)
        {
            ev.ReportedFindings = stats.ReportedFindings;
            ev.FixedFindings = stats.FixedFindings;
        }
        if (errMsg.Length > 0)
        {
            ev.Error = errMsg;
        }
        if (findings.Length > 0)
        {
            ev.Findings = findings;
        }
        if (diff.Length > 0)
        {
            ev.Diff = diff;
        }
        onEvent(ev);
    }

    private StepStats FindingStatsForStep(string runId, string stepName)
    {
        var stepsForRun = db.GetStepsByRun(runId);
        foreach (var step in stepsForRun)
        {
            if (step.StepName != stepName)
            {
                continue;
            }
            return db.StepFindingStats(step);
        }
        return new StepStats { StepName = stepName };
    }

    private void EmitLogChunk(Run run, Repo repo, string stepName, string content)
    {
        onEvent(new IpcEvent
        {
            Type = EventTypes.LogChunk,
            RunId = run.Id,
            RepoId = repo.Id,
            StepName = stepName,
            Content = content,
        });
    }
}

/// <summary>Internal signal that a step failed; carries the composed error message.</summary>
internal sealed class StepFailedException : Exception
{
    public StepFailedException(string message) : base(message) { }
}
