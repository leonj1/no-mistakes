using System.Diagnostics;
using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Daemon;
using NoMistakes.Data;
using NoMistakes.Git;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// notify-push wiring (slice 7e.2): the push-option parse/format helpers
/// (Go internal/cli daemon_cmd_test.go), the notify-push operation and CLI
/// command reaching a live daemon over IPC, and the slice-4 post-receive hook
/// script driving the real CLI binary end to end.
/// </summary>
[Collection("daemon")]
public class DaemonNotifyPushTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string OldSha = "1111111111111111111111111111111111111111";
    private const string NewSha = "2222222222222222222222222222222222222222";
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    [Fact]
    public void ParseSkipPushOptions_ExtractsStepsAndIgnoresForeignOptions()
    {
        var got = DaemonNotifyPush.ParseSkipPushOptions(new[]
        {
            "ci.skip",
            "no-mistakes.skip=test,lint",
        });
        Assert.Equal(new[] { StepName.Test, StepName.Lint }, got);
    }

    [Fact]
    public void ParseSkipPushOptions_RejectsUnknownStep()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DaemonNotifyPush.ParseSkipPushOptions(new[] { "no-mistakes.skip=test,deploy" }));
        Assert.Contains("unknown step \"deploy\"", ex.Message);
    }

    [Fact]
    public void ParseSkipPushOptions_DedupesAcrossOptions()
    {
        var got = DaemonNotifyPush.ParseSkipPushOptions(new[]
        {
            "no-mistakes.skip=test,lint",
            "no-mistakes.skip=lint,ci",
        });
        Assert.Equal(new[] { StepName.Test, StepName.Lint, StepName.Ci }, got);
    }

    [Fact]
    public void FormatSkipPushOptions_JoinsSteps()
    {
        var got = DaemonNotifyPush.FormatSkipPushOptions(new[] { StepName.Test, StepName.Lint });
        Assert.Equal(new[] { "no-mistakes.skip=test,lint" }, got);
    }

    [Fact]
    public void IntentPushOption_RoundTrips()
    {
        // Multi-line, comma- and colon-bearing intent must survive the
        // line-oriented push-option transport intact.
        var intent = "add retry to the uploader\n\nwhy: flaky network, commas, colons: ok";
        var opt = DaemonNotifyPush.FormatIntentPushOption(intent);
        Assert.NotEqual(string.Empty, opt);
        var got = DaemonNotifyPush.ParseIntentPushOptions(new[] { "no-mistakes.skip=test", opt });
        Assert.Equal(intent, got);
    }

    [Fact]
    public void FormatIntentPushOption_BlankIntentIsEmpty()
    {
        Assert.Equal(string.Empty, DaemonNotifyPush.FormatIntentPushOption("   "));
    }

    [Fact]
    public void ParseIntentPushOptions_NoIntentIsEmpty()
    {
        var got = DaemonNotifyPush.ParseIntentPushOptions(new[] { "no-mistakes.skip=test", "ci.skip" });
        Assert.Equal(string.Empty, got);
    }

    [Fact]
    public void ParseIntentPushOptions_RejectsCorruptEncoding()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DaemonNotifyPush.ParseIntentPushOptions(new[] { "no-mistakes.intent=!!!not-base64!!!" }));
        Assert.Contains("decode intent push option", ex.Message);
    }

    [Fact]
    public void RepoIdFromGatePath_ExtractsIdAndRejectsNonGatePaths()
    {
        Assert.Equal("repo123", RunManager.RepoIdFromGatePath("/home/u/.no-mistakes/repos/repo123.git"));
        var ex = Assert.Throws<InvalidOperationException>(() => RunManager.RepoIdFromGatePath("."));
        Assert.Equal("invalid gate path: .", ex.Message);
    }

    [Fact]
    public void BranchFromRef_StripsHeadsPrefix()
    {
        Assert.Equal("main", RunManager.BranchFromRef("refs/heads/main"));
        Assert.Equal("main", RunManager.BranchFromRef("main"));
    }

    [Fact]
    public async Task NotifyPushStartsRunOnDaemon()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var executed = new TaskCompletionSource<Run>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, (run, _, _) =>
        {
            executed.TrySetResult(run);
            return Task.CompletedTask;
        });

        await using var host = await StartDaemonAsync(paths, db, manager);
        var result = await DaemonNotifyPush.NotifyPushAsync(
            paths,
            gate: Path.Combine(tmp.Path, "repos", repo.Id + ".git"),
            refName: "refs/heads/feature",
            oldSha: OldSha,
            newSha: NewSha,
            pushOptions: Array.Empty<string>()).WaitAsync(Timeout);

        Assert.NotEqual(string.Empty, result.RunId);
        var run = await executed.Task.WaitAsync(Timeout);
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal(repo.Id, run.RepoId);
        Assert.Equal("feature", run.Branch);
        Assert.Equal(NewSha, run.HeadSha);
        Assert.Equal(OldSha, run.BaseSha);
    }

    [Fact]
    public async Task NotifyPushRejectsRefDeletion()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);

        await using var host = await StartDaemonAsync(paths, db, manager);
        var ex = await Assert.ThrowsAsync<IpcRpcException>(() => DaemonNotifyPush.NotifyPushAsync(
            paths,
            gate: Path.Combine(tmp.Path, "repos", repo.Id + ".git"),
            refName: "refs/heads/feature",
            oldSha: NewSha,
            newSha: ZeroSha,
            pushOptions: Array.Empty<string>()).WaitAsync(Timeout));
        Assert.Contains("ref deletion push, no pipeline to run", ex.Message);
        Assert.Empty(db.GetRunsByRepo(repo.Id));
    }

    [Fact]
    public async Task NotifyPushRejectsUnknownGateRepo()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var manager = new RunManager(db, (_, _, _) => Task.CompletedTask);

        await using var host = await StartDaemonAsync(paths, db, manager);
        var ex = await Assert.ThrowsAsync<IpcRpcException>(() => DaemonNotifyPush.NotifyPushAsync(
            paths,
            gate: Path.Combine(tmp.Path, "repos", "nope.git"),
            refName: "refs/heads/feature",
            oldSha: OldSha,
            newSha: NewSha,
            pushOptions: Array.Empty<string>()).WaitAsync(Timeout));
        Assert.Contains("unknown repo for gate", ex.Message);
    }

    [Fact]
    public async Task NotifyPushWithStoppedDaemonFailsToConnect()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        paths.EnsureDirs();

        var ex = await Assert.ThrowsAsync<IOException>(() => DaemonNotifyPush.NotifyPushAsync(
            paths, "/x/repos/r.git", "refs/heads/feature", OldSha, NewSha,
            Array.Empty<string>()).WaitAsync(Timeout));
        Assert.Contains("connect to daemon", ex.Message);
    }

    [Fact]
    public async Task CliDaemonNotifyPushCommandReachesDaemon()
    {
        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var executed = new TaskCompletionSource<Run>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, (run, _, _) =>
        {
            executed.TrySetResult(run);
            return Task.CompletedTask;
        });

        await using var host = await StartDaemonAsync(paths, db, manager);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code;
        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        Environment.SetEnvironmentVariable("NM_HOME", tmp.Path);
        try
        {
            code = CliApp.Run(new[]
            {
                "daemon", "notify-push",
                "--gate", Path.Combine(tmp.Path, "repos", repo.Id + ".git"),
                "--ref", "refs/heads/feature",
                "--old", OldSha,
                "--new", NewSha,
                "--push-option", "no-mistakes.skip=ci",
            }, stdout, stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.ToString());
        var run = await executed.Task.WaitAsync(Timeout);
        Assert.Equal("feature", run.Branch);
        Assert.Equal(NewSha, run.HeadSha);
    }

    [Fact]
    public void CliDaemonNotifyPushRequiresFlags()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliApp.Run(new[] { "daemon", "notify-push", "--gate", "/x/repos/r.git" }, stdout, stderr);
        Assert.Equal(1, code);
        Assert.Contains("required flag(s)", stderr.ToString());
        Assert.Contains("\"ref\"", stderr.ToString());
        Assert.Contains("\"old\"", stderr.ToString());
        Assert.Contains("\"new\"", stderr.ToString());
    }

    [Fact]
    public void CliDaemonNotifyPushRejectsUnknownSkipStepBeforeDialing()
    {
        // Push-option validation fails before any daemon contact, so no
        // daemon is needed and the error is the parse error, not a dial error.
        using var tmp = new TempDir();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code;
        var savedHome = Environment.GetEnvironmentVariable("NM_HOME");
        Environment.SetEnvironmentVariable("NM_HOME", tmp.Path);
        try
        {
            code = CliApp.Run(new[]
            {
                "daemon", "notify-push",
                "--gate", "/x/repos/r.git", "--ref", "refs/heads/f",
                "--old", OldSha, "--new", NewSha,
                "--push-option", "no-mistakes.skip=deploy",
            }, stdout, stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NM_HOME", savedHome);
        }
        Assert.Equal(1, code);
        Assert.Contains("unknown step \"deploy\"", stderr.ToString());
    }

    [Fact]
    public async Task PostReceiveHookInvocationReachesDaemon()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the post-receive hook is a POSIX shell script
        }

        using var tmp = new TempDir();
        var paths = Paths.WithRoot(tmp.Path);
        using var db = DataTestSupport.OpenTestDb(tmp);
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");

        var executed = new TaskCompletionSource<Run>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(db, (run, _, _) =>
        {
            executed.TrySetResult(run);
            return Task.CompletedTask;
        });

        await using var host = await StartDaemonAsync(paths, db, manager);

        // Real bare gate repo so the hook's `git rev-parse --absolute-git-dir`
        // resolves the gate path the same way it does in production.
        var bareDir = Path.Combine(tmp.Path, "repos", repo.Id + ".git");
        Directory.CreateDirectory(bareDir);
        await new GitClient().RunAsync(bareDir, "init", "--bare", ".");

        // Shim standing in for the installed no-mistakes binary: points the
        // real CLI (the slice's `daemon notify-push` command) at this test's
        // NM_HOME. AppContext.BaseDirectory holds the Cli project output
        // because the test project references it.
        var cliDll = Path.Combine(AppContext.BaseDirectory, "no-mistakes.dll");
        Assert.True(File.Exists(cliDll), $"CLI assembly not found at {cliDll}");
        var shim = Path.Combine(tmp.Path, "no-mistakes");
        File.WriteAllText(shim,
            "#!/bin/sh\n" +
            $"NM_HOME={PostReceiveHook.ShellSingleQuote(tmp.Path)}\n" +
            "export NM_HOME\n" +
            $"exec dotnet {PostReceiveHook.ShellSingleQuote(cliDll)} \"$@\"\n");
        File.SetUnixFileMode(shim, (UnixFileMode)0b111_101_101); // 0755

        var hookPath = Path.Combine(bareDir, "hooks", "post-receive");
        Directory.CreateDirectory(Path.Combine(bareDir, "hooks"));
        File.WriteAllText(hookPath, PostReceiveHook.ScriptFor(shim));
        File.SetUnixFileMode(hookPath, (UnixFileMode)0b111_101_101);

        var psi = new ProcessStartInfo("/bin/sh", hookPath)
        {
            WorkingDirectory = bareDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_PUSH_OPTION_COUNT"] = "1";
        psi.Environment["GIT_PUSH_OPTION_0"] = "no-mistakes.skip=ci";
        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync($"{OldSha} {NewSha} refs/heads/feature\n");
        proc.StandardInput.Close();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        // Generous bound: the shim cold-starts a child dotnet process.
        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var hookStderr = await stderrTask;

        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("Pipeline started", hookStderr);
        Assert.DoesNotContain("notify-push failed", hookStderr);

        var run = await executed.Task.WaitAsync(Timeout);
        Assert.Equal(repo.Id, run.RepoId);
        Assert.Equal("feature", run.Branch);
        Assert.Equal(NewSha, run.HeadSha);
        Assert.Equal(OldSha, run.BaseSha);
    }

    private static async Task<DaemonHandle> StartDaemonAsync(Paths paths, Database db, RunManager manager)
    {
        var daemon = new DaemonHost(paths, db);
        RunIpcHandlers.Register(daemon.Server, manager, db);
        var run = Task.Run(daemon.RunAsync);
        await daemon.Ready.WaitAsync(Timeout);
        return new DaemonHandle { Daemon = daemon, Run = run };
    }

    private sealed class DaemonHandle : IAsyncDisposable
    {
        public required DaemonHost Daemon { get; init; }
        public required Task Run { get; init; }

        public async ValueTask DisposeAsync()
        {
            Daemon.Shutdown();
            await Run.WaitAsync(Timeout);
        }
    }
}
