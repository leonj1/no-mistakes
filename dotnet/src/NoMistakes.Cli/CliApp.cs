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

        stderr.WriteLine($"unknown command: {args[0]}");
        stderr.WriteLine("Run 'no-mistakes --help' for usage.");
        return 2;
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
