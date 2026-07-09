using NoMistakes.Core;

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

        stderr.WriteLine($"unknown command: {args[0]}");
        stderr.WriteLine("Run 'no-mistakes --help' for usage.");
        return 2;
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
