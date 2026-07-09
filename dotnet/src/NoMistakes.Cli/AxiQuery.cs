using NoMistakes.Core;
using NoMistakes.Data;

namespace NoMistakes.Cli;

/// <summary>
/// A finished axi command result: the TOON document to write to stdout and the
/// process exit code. Ports the emitDoc/emitError + exitError pair from Go's
/// cobra layer into a value the command dispatcher renders.
/// </summary>
public sealed record AxiOutput(string Doc, int ExitCode)
{
    public static AxiOutput Ok(params ToonField[] fields) => new(AxiRender.Doc(fields), 0);

    /// <summary>Structured TOON error on stdout plus a non-zero exit code (Go emitError).</summary>
    public static AxiOutput Error(int code, string msg, params string[] help)
    {
        var fields = new List<ToonField> { new("error", msg) };
        if (help.Length > 0)
        {
            fields.Add(new ToonField("help", help.ToList()));
        }
        return new AxiOutput(AxiRender.Doc(fields.ToArray()), code);
    }
}

/// <summary>
/// The read-only axi commands - home, status, and logs - as pure document
/// builders over the database and paths, rendering via the slice-8a TOON
/// layer. Ports Go's internal/cli/axi.go (home) and axi_query.go (status,
/// logs). Environment resolution (cwd repo lookup, daemon liveness, current
/// branch) lives in AxiEnv/CliApp so these stay deterministic under test.
/// </summary>
public static class AxiQuery
{
    /// <summary>
    /// Caps the recent-runs table on the home view. High enough to cover normal
    /// history in one call, per the AXI minimal-call convention.
    /// </summary>
    public const int RecentRunsHomeLimit = 10;

    /// <summary>How many trailing log lines `axi logs` shows without --full.</summary>
    public const int LogTailLines = 40;

    /// <summary>
    /// The skill's trigger-shaped description, reused as the home view's
    /// `description:` field so the two never drift. Byte-for-byte copy of Go's
    /// skill.Description; slice 16b's generated-skill source of truth should
    /// absorb this constant when it lands.
    /// </summary>
    public const string SkillDescription =
        "Validate your code changes through the no-mistakes pipeline - automated code review, tests, lint, docs, push, PR, and CI - before they reach the configured push target. Use when the user asks to run no-mistakes, gate or ship or validate their changes, push safely, asks you to do a task and then validate it, or invokes /no-mistakes.";

    internal const string ValidStepsHelp = "Valid steps: intent, rebase, review, test, document, lint, push, pr, ci";

    /// <summary>Ports Go's startRunHelp (also the no-run logs help).</summary>
    internal const string StartRunHelp =
        "Run no-mistakes axi run --intent \"the user's goal\" --yes to validate the current branch";

    /// <summary>
    /// Renders the content-first home view: tool identity, repo, daemon state,
    /// the active run (if any) with its gate, and recent runs - all from the
    /// local database so it works whether or not the daemon is running.
    /// </summary>
    public static AxiOutput Home(Database db, Repo repo, string branch, bool daemonRunning, string binDisplay)
    {
        var fields = new List<ToonField>
        {
            new("bin", binDisplay),
            new("description", SkillDescription),
            new("repo", repo.WorkingPath),
            new("current_branch", branch.Length == 0 ? "unknown" : branch),
            new("daemon", daemonRunning ? "running" : "stopped"),
        };

        var currentActive = branch.Length > 0 ? db.GetActiveRun(repo.Id, branch) : null;
        Run? otherActive = null;
        if (currentActive == null)
        {
            otherActive = db.GetActiveRun(repo.Id, "");
            if (otherActive != null && otherActive.Branch == branch)
            {
                otherActive = null;
            }
        }

        var gated = false;
        if (currentActive != null)
        {
            var rv = RunView.FromDb(currentActive, db.GetStepsByRun(currentActive.Id));
            fields.Add(AxiRender.RunObjectFieldWithKey("active_run", rv));
            if (rv.AwaitingStep() is { } gate)
            {
                gated = true;
                fields.AddRange(AxiRender.GateFields(gate));
            }
        }
        else if (otherActive != null)
        {
            var rv = RunView.FromDb(otherActive, db.GetStepsByRun(otherActive.Id));
            fields.Add(AxiRender.RunObjectFieldWithKey("other_branch_active_run", rv));
        }

        fields.AddRange(RunsFields(db.GetRunsByRepo(repo.Id), RecentRunsHomeLimit));

        var help = new List<string>();
        if (currentActive == null)
        {
            help.Add("Run `no-mistakes axi run --intent \"<what the user set out to accomplish>\"` to validate your changes");
            if (otherActive != null)
            {
                help.Add($"Another active run is on {otherActive.Branch}; leave it alone unless you are working on that branch");
            }
        }
        else if (gated)
        {
            help.Add("Run `no-mistakes axi respond --action approve` to clear the current gate");
        }
        else
        {
            help.Add("Run `no-mistakes axi status` to inspect the active run");
        }
        help.Add("How to drive the pipeline: `no-mistakes axi run --help`, or the `/no-mistakes` skill (loaded when you invoke `/no-mistakes`)");
        fields.Add(new ToonField("help", help));

        return AxiOutput.Ok(fields.ToArray());
    }

    /// <summary>
    /// Renders a recent-runs table with an aggregate count, showing at most
    /// limit rows newest-first. Ports Go's runsFields.
    /// </summary>
    internal static List<ToonField> RunsFields(IReadOnlyList<Run> runs, int limit)
    {
        if (runs.Count == 0)
        {
            return [new ToonField("runs", "0 runs yet in this repository")];
        }
        var shown = limit > 0 && runs.Count > limit ? runs.Take(limit).ToList() : (IReadOnlyList<Run>)runs;
        var rows = new List<ToonObject>(shown.Count);
        foreach (var r in shown)
        {
            rows.Add(new ToonObject(
                new ToonField("id", r.Id),
                new ToonField("branch", r.Branch),
                new ToonField("status", r.Status),
                new ToonField("head", AxiRender.ShortSha(r.HeadSha)),
                new ToonField("pr", r.PrUrl ?? "")));
        }
        return
        [
            new ToonField("count", $"{shown.Count} of {runs.Count} total"),
            new ToonField("runs", rows),
        ];
    }

    /// <summary>Shows the active (or most recent) run in detail. Ports Go's runAxiStatus.</summary>
    public static AxiOutput Status(Database db, Repo repo, string? runId, string branch)
    {
        runId ??= "";
        var run = ResolveRun(db, repo, runId, branch);
        if (run == null)
        {
            if (runId.Length > 0)
            {
                return AxiOutput.Error(1, $"run \"{runId}\" not found");
            }
            return AxiOutput.Ok(
                new ToonField("runs", "0 runs yet in this repository"),
                new ToonField("help", new List<string> { StartRunHelp }));
        }

        var rv = RunView.FromDb(run, db.GetStepsByRun(run.Id));
        var fields = new List<ToonField> { AxiRender.RunObjectField(rv) };
        if (rv.AwaitingStep() is { } gate)
        {
            fields.AddRange(AxiRender.GateFields(gate));
        }
        else if (AxiRender.TerminalStatus(rv.Status))
        {
            fields.Add(new ToonField("outcome", OutcomeFor(rv.Status)));
            if (!string.IsNullOrEmpty(run.Error))
            {
                fields.Add(new ToonField("error", run.Error));
            }
        }
        return AxiOutput.Ok(fields.ToArray());
    }

    /// <summary>
    /// Validates the `axi logs` --step flag, returning the structured error
    /// output for a missing or unknown step and null when valid. Runs before
    /// any environment is opened, matching Go's runAxiLogs order.
    /// </summary>
    public static AxiOutput? ValidateLogsStep(string step)
    {
        if (step.Length == 0)
        {
            return AxiOutput.Error(2, "--step is required", ValidStepsHelp);
        }
        if (!StepName.All.Contains(step))
        {
            return AxiOutput.Error(2, $"unknown step \"{step}\"", ValidStepsHelp);
        }
        return null;
    }

    /// <summary>
    /// Shows the log output of one pipeline step. The step must already have
    /// passed <see cref="ValidateLogsStep"/>. Ports Go's runAxiLogs.
    /// </summary>
    public static AxiOutput Logs(Paths paths, Database db, Repo repo, string step, string? runId, bool full, string branch)
    {
        var run = ResolveRun(db, repo, runId ?? "", branch);
        if (run == null)
        {
            return AxiOutput.Error(1, "no run found to read logs from", StartRunHelp);
        }

        var path = Path.Combine(paths.RunLogDir(run.Id), step + ".log");
        var fields = new List<ToonField>
        {
            new("step", step),
            new("run", run.Id),
        };
        string data;
        try
        {
            data = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            fields.Add(new ToonField("log", $"no log recorded for step \"{step}\" in this run"));
            return AxiOutput.Ok(fields.ToArray());
        }
        catch (Exception ex)
        {
            return AxiOutput.Error(1, $"read log: {ex.Message}");
        }

        var lines = SplitLogLines(data);
        if (!full && lines.Count > LogTailLines)
        {
            var shown = lines.Skip(lines.Count - LogTailLines).ToList();
            fields.Add(new ToonField("lines", $"{shown.Count} of {lines.Count} total (tail)"));
            fields.Add(new ToonField("log", LogRows(shown)));
            fields.Add(new ToonField("help", new List<string>
            {
                $"Run `no-mistakes axi logs --step {step} --full` to see the entire log",
            }));
            return AxiOutput.Ok(fields.ToArray());
        }
        fields.Add(new ToonField("lines", $"{lines.Count} total"));
        fields.Add(new ToonField("log", LogRows(lines)));
        return AxiOutput.Ok(fields.ToArray());
    }

    /// <summary>
    /// Wraps log lines as single-column rows so the encoder renders them as a
    /// block array (one line per row) rather than a single inline row.
    /// </summary>
    internal static List<ToonObject> LogRows(IReadOnlyList<string> lines)
    {
        var rows = new List<ToonObject>(lines.Count);
        foreach (var l in lines)
        {
            rows.Add(new ToonObject(new ToonField("line", l)));
        }
        return rows;
    }

    /// <summary>
    /// Picks the run to inspect: an explicit ID, else the current branch's
    /// active (then latest) run, else the repo's active run, else the most
    /// recent run. Returns null when none exist. Ports Go's resolveRun.
    /// </summary>
    internal static Run? ResolveRun(Database db, Repo repo, string runId, string branch)
    {
        if (runId.Length > 0)
        {
            return db.GetRun(runId);
        }
        if (branch.Length > 0)
        {
            var branchActive = db.GetActiveRun(repo.Id, branch);
            if (branchActive != null)
            {
                return branchActive;
            }
            foreach (var run in db.GetRunsByRepo(repo.Id))
            {
                if (run.Branch == branch)
                {
                    return run;
                }
            }
        }
        var active = db.GetActiveRun(repo.Id, "");
        if (active != null)
        {
            return active;
        }
        var runs = db.GetRunsByRepo(repo.Id);
        return runs.Count == 0 ? null : runs[0];
    }

    /// <summary>Maps a terminal run status onto an agent-facing outcome word.</summary>
    internal static string OutcomeFor(string status) => status switch
    {
        RunStatus.Completed => "passed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => status,
    };

    internal static IReadOnlyList<string> SplitLogLines(string s)
    {
        s = s.TrimEnd('\n');
        return s.Length == 0 ? [] : s.Split('\n');
    }

    /// <summary>
    /// Returns an actionable hint when the failure is an uninitialized repo,
    /// and nothing otherwise. Ports Go's repoInitHelp.
    /// </summary>
    internal static string[] RepoInitHelp(string errMsg) =>
        errMsg.Contains("not initialized")
            ? ["Run `no-mistakes init` to set up the gate in this repository"]
            : [];

    /// <summary>
    /// The absolute path of the running binary, falling back to the invoked
    /// name if it cannot be resolved. Ports Go's executablePath.
    /// </summary>
    internal static string ExecutablePath()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            var args = Environment.GetCommandLineArgs();
            return args.Length > 0 ? args[0] : "no-mistakes";
        }
        try
        {
            var resolved = new FileInfo(exe).ResolveLinkTarget(returnFinalTarget: true);
            return resolved?.FullName ?? exe;
        }
        catch (IOException)
        {
            return exe;
        }
    }

    /// <summary>Rewrites a leading home directory to ~ for compact display.</summary>
    internal static string CollapseHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? "";
        }
        if (home.Length == 0)
        {
            return path;
        }
        if (path == home)
        {
            return "~";
        }
        var prefix = home + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
