using NoMistakes.Core;
using NoMistakes.Daemon;

namespace NoMistakes.Cli;

public sealed class CliApp
{
    private const string Description = "Local Git proxy that validates code before pushing to the configured target";

    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    private readonly BuildInfoOptions buildInfo;

    public CliApp(TextWriter stdout, TextWriter stderr, BuildInfoOptions? buildInfo = null)
    {
        this.stdout = stdout;
        this.stderr = stderr;
        this.buildInfo = buildInfo ?? BuildInfoOptions.Defaults;
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        return new CliApp(stdout, stderr).Run(args);
    }

    public int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            WriteHelp();
            return 0;
        }

        if (IsVersion(args[0]))
        {
            stdout.WriteLine($"no-mistakes version {BuildInfo.Format(buildInfo)}");
            return 0;
        }

        if (args[0] == "daemon" && args.Count >= 2 && args[1] == "notify-push")
        {
            return RunNotifyPush(args.Skip(2).ToList());
        }

        if (args[0] == "axi")
        {
            return RunAxi(args.Skip(1).ToList());
        }

        stderr.WriteLine($"unknown command: {args[0]}");
        stderr.WriteLine("Run 'no-mistakes --help' for usage.");
        return 2;
    }

    /// <summary>
    /// The agent-facing `axi` command tree: home (bare `axi`), `axi status`,
    /// `axi logs`, `axi run`, and `axi abort`. TOON on stdout, structured
    /// errors, explicit exit codes. Ports Go's newAxiCmd dispatch; respond
    /// arrives in slice 8c.2.
    /// </summary>
    private int RunAxi(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return RunAxiHome();
        }
        switch (args[0])
        {
            case "status":
                return RunAxiStatus(args.Skip(1).ToList());
            case "logs":
                return RunAxiLogs(args.Skip(1).ToList());
            case "run":
                return RunAxiRun(args.Skip(1).ToList());
            case "abort":
                return RunAxiAbort(args.Skip(1).ToList());
            default:
                stderr.WriteLine($"unknown command: axi {args[0]}");
                stderr.WriteLine("Run 'no-mistakes --help' for usage.");
                return 2;
        }
    }

    private int Emit(AxiOutput output)
    {
        stdout.Write(output.Doc);
        return output.ExitCode;
    }

    private int RunAxiHome()
    {
        AxiEnv env;
        try
        {
            env = AxiEnv.OpenAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Emit(AxiOutput.Error(1, ex.Message, AxiQuery.RepoInitHelp(ex.Message)));
        }
        using (env)
        {
            var daemonRunning = DaemonStatus.IsRunningAsync(env.Paths).GetAwaiter().GetResult();
            var branch = AxiEnv.CurrentBranchForRunResolveAsync().GetAwaiter().GetResult();
            var bin = AxiQuery.CollapseHome(AxiQuery.ExecutablePath());
            return Emit(AxiQuery.Home(env.Db, env.Repo, branch, daemonRunning, bin));
        }
    }

    private int RunAxiStatus(IReadOnlyList<string> args)
    {
        string runId = "";
        try
        {
            ParseAxiFlags(args, new Dictionary<string, Action<string>>
            {
                ["--run"] = v => runId = v,
            });
        }
        catch (ArgumentException ex)
        {
            return Emit(AxiOutput.Error(2, ex.Message));
        }

        AxiEnv env;
        try
        {
            env = AxiEnv.OpenAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Emit(AxiOutput.Error(1, ex.Message, AxiQuery.RepoInitHelp(ex.Message)));
        }
        using (env)
        {
            var branch = AxiEnv.CurrentBranchForRunResolveAsync().GetAwaiter().GetResult();
            return Emit(AxiQuery.Status(env.Db, env.Repo, runId, branch));
        }
    }

    private int RunAxiLogs(IReadOnlyList<string> args)
    {
        string step = "", runId = "";
        var full = false;
        try
        {
            ParseAxiFlags(
                args,
                new Dictionary<string, Action<string>>
                {
                    ["--step"] = v => step = v,
                    ["--run"] = v => runId = v,
                },
                new Dictionary<string, Action>
                {
                    ["--full"] = () => full = true,
                });
        }
        catch (ArgumentException ex)
        {
            return Emit(AxiOutput.Error(2, ex.Message));
        }

        step = step.Trim();
        if (AxiQuery.ValidateLogsStep(step) is { } invalid)
        {
            return Emit(invalid);
        }

        AxiEnv env;
        try
        {
            env = AxiEnv.OpenAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Emit(AxiOutput.Error(1, ex.Message, AxiQuery.RepoInitHelp(ex.Message)));
        }
        using (env)
        {
            var branch = AxiEnv.CurrentBranchForRunResolveAsync().GetAwaiter().GetResult();
            return Emit(AxiQuery.Logs(env.Paths, env.Db, env.Repo, step, runId, full, branch));
        }
    }

    /// <summary>
    /// The `axi run` command: trigger a pipeline run for the current branch
    /// and drive it to a decision point or outcome. Ports Go's newAxiRunCmd.
    /// </summary>
    private int RunAxiRun(IReadOnlyList<string> args)
    {
        var autoYes = false;
        string skipValue = "", intent = "";
        try
        {
            ParseAxiFlags(
                args,
                new Dictionary<string, Action<string>>
                {
                    ["--skip"] = v => skipValue = v,
                    ["--intent"] = v => intent = v,
                },
                new Dictionary<string, Action>
                {
                    ["--yes"] = () => autoYes = true,
                    ["-y"] = () => autoYes = true,
                });
        }
        catch (ArgumentException ex)
        {
            return Emit(AxiOutput.Error(2, ex.Message));
        }

        List<string> skipSteps;
        try
        {
            skipSteps = DaemonNotifyPush.ParseSkipSteps(skipValue);
        }
        catch (ArgumentException ex)
        {
            return Emit(AxiOutput.Error(2, ex.Message, AxiQuery.ValidStepsHelp));
        }

        AxiEnv env;
        try
        {
            env = AxiEnv.OpenAsync(ensureDaemonConn: true).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Emit(AxiOutput.Error(1, ex.Message, AxiQuery.RepoInitHelp(ex.Message)));
        }
        using (env)
        {
            return Emit(AxiDrive.RunAsync(env, stderr, autoYes, skipSteps, intent).GetAwaiter().GetResult());
        }
    }

    /// <summary>
    /// The `axi abort` command: cancel the active run on the current branch,
    /// or a specific run by id with --run. Ports Go's newAxiAbortCmd.
    /// </summary>
    private int RunAxiAbort(IReadOnlyList<string> args)
    {
        var runId = "";
        try
        {
            ParseAxiFlags(args, new Dictionary<string, Action<string>>
            {
                ["--run"] = v => runId = v,
            });
        }
        catch (ArgumentException ex)
        {
            return Emit(AxiOutput.Error(2, ex.Message));
        }

        runId = runId.Trim();
        if (runId.Length > 0)
        {
            // By-id abort needs only NM_HOME plus the daemon, never a
            // repo/branch/worktree - it reaps orphaned monitors from outside.
            try
            {
                var paths = Paths.New();
                var outcome = AxiAbort.AbortByRunIdAsync(paths, runId).GetAwaiter().GetResult();
                return Emit(AxiDrive.RenderAbortByIdOutcome(outcome));
            }
            catch (Exception ex)
            {
                return Emit(AxiOutput.Error(1, $"abort run: {ex.Message}"));
            }
        }

        AxiEnv env;
        try
        {
            env = AxiEnv.OpenAsync(ensureDaemonConn: true).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Emit(AxiOutput.Error(1, ex.Message, AxiQuery.RepoInitHelp(ex.Message)));
        }
        using (env)
        {
            return Emit(AxiDrive.AbortAsync(env).GetAwaiter().GetResult());
        }
    }

    /// <summary>
    /// Minimal flag loop for the axi subcommands: value flags consume the next
    /// argument, bool flags consume none. Unknown flags and missing values
    /// throw ArgumentException with cobra-shaped messages.
    /// </summary>
    private static void ParseAxiFlags(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, Action<string>> valueFlags,
        IReadOnlyDictionary<string, Action>? boolFlags = null)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (boolFlags != null && boolFlags.TryGetValue(args[i], out var setBool))
            {
                setBool();
                continue;
            }
            if (valueFlags.TryGetValue(args[i], out var setValue))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"flag needs an argument: {args[i]}");
                }
                setValue(args[++i]);
                continue;
            }
            throw new ArgumentException($"unknown flag: {args[i]}");
        }
    }

    /// <summary>
    /// The hidden `daemon notify-push` command the post-receive hook invokes.
    /// Flags mirror Go's cobra definition: required --gate/--ref/--old/--new
    /// plus repeatable --push-option. Not listed in help (Hidden in Go).
    /// </summary>
    private int RunNotifyPush(IReadOnlyList<string> args)
    {
        string? gate = null, refName = null, oldSha = null, newSha = null;
        var pushOptions = new List<string>();
        try
        {
            for (var i = 0; i < args.Count; i++)
            {
                string TakeValue(string flag)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new ArgumentException($"flag needs an argument: {flag}");
                    }
                    return args[++i];
                }
                switch (args[i])
                {
                    case "--gate":
                        gate = TakeValue("--gate");
                        break;
                    case "--ref":
                        refName = TakeValue("--ref");
                        break;
                    case "--old":
                        oldSha = TakeValue("--old");
                        break;
                    case "--new":
                        newSha = TakeValue("--new");
                        break;
                    case "--push-option":
                        pushOptions.Add(TakeValue("--push-option"));
                        break;
                    default:
                        throw new ArgumentException($"unknown flag: {args[i]}");
                }
            }

            var missing = new List<string>();
            if (gate == null) missing.Add("gate");
            if (refName == null) missing.Add("ref");
            if (oldSha == null) missing.Add("old");
            if (newSha == null) missing.Add("new");
            if (missing.Count > 0)
            {
                throw new ArgumentException(
                    "required flag(s) " + string.Join(", ", missing.Select(f => $"\"{f}\"")) + " not set");
            }

            var paths = Paths.New();
            DaemonNotifyPush.NotifyPushAsync(paths, gate!, refName!, oldSha!, newSha!, pushOptions)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string arg)
    {
        return arg is "--help" or "-h" or "help";
    }

    private static bool IsVersion(string arg)
    {
        return arg is "--version" or "version";
    }

    private void WriteHelp()
    {
        stdout.WriteLine(Description);
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  no-mistakes [command]");
        stdout.WriteLine();
        stdout.WriteLine("Available Commands:");
        stdout.WriteLine("  help        Help about any command");
        stdout.WriteLine("  version     Print build version");
        stdout.WriteLine();
        stdout.WriteLine("Flags:");
        stdout.WriteLine("  -h, --help      help for no-mistakes");
        stdout.WriteLine("      --version   version for no-mistakes");
    }
}
