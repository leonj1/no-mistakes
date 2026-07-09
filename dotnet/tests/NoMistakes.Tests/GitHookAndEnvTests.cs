using NoMistakes.Git;
using Xunit;

namespace NoMistakes.Tests;

public class GitEnvTests
{
    [Fact]
    public void NonInteractiveEnv_SetsGitOverrides()
    {
        var env = GitClient.NonInteractiveEnv(null);
        Assert.Equal("true", env["GIT_EDITOR"]);
        Assert.Equal("true", env["GIT_SEQUENCE_EDITOR"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void NonInteractiveEnv_SetsAbsolutePwdForDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // PWD injection is non-Windows only, matching Go.
        }
        using var tmp = new TempDir();
        var env = GitClient.NonInteractiveEnv(tmp.Path);
        Assert.True(env.ContainsKey("PWD"));
        Assert.True(Path.IsPathRooted(env["PWD"]));
    }

    [Fact]
    public void NonInteractiveEnv_AbsolutizesRelativeDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var env = GitClient.NonInteractiveEnv(".");
        Assert.True(Path.IsPathRooted(env["PWD"]));
        Assert.NotEqual(".", env["PWD"]);
    }

    [Fact]
    public void NonInteractiveEnv_EmptyDirLeavesPwdUnset()
    {
        var env = GitClient.NonInteractiveEnv("");
        Assert.False(env.ContainsKey("PWD"));
    }
}

public class RedactorTests
{
    [Fact]
    public void RedactText_HidesUrlUserInfo()
    {
        var input = "failed: https://user:token@example.com/repo.git not found";
        var got = Redactor.RedactText(input);
        Assert.DoesNotContain("token", got);
        Assert.Contains("redacted@example.com", got);
    }

    [Fact]
    public void RedactText_LeavesCredentialFreeUrlUnchanged()
    {
        var input = "cloning https://example.com/repo.git";
        Assert.Equal(input, Redactor.RedactText(input));
    }

    [Fact]
    public void RedactText_LeavesNonUrlUnchanged()
    {
        Assert.Equal("no urls here", Redactor.RedactText("no urls here"));
    }
}

public class PostReceiveHookTests
{
    [Fact]
    public void Script_ContainsManagedMarkersAndBanner()
    {
        var script = PostReceiveHook.ScriptFor("/usr/local/bin/no-mistakes");
        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("# no-mistakes post-receive hook", script);
        Assert.Contains("daemon notify-push", script);
        Assert.Contains("Pipeline started", script);
        Assert.Contains("NM_BIN='/usr/local/bin/no-mistakes'", script);
    }

    [Fact]
    public void Script_DoesNotEvaluatePushOptions()
    {
        // Push options are read via printenv into positional args, never eval'd.
        var script = PostReceiveHook.ScriptFor("no-mistakes");
        Assert.Contains("printenv \"GIT_PUSH_OPTION_$i\"", script);
        Assert.DoesNotContain("eval", script);
    }

    [Fact]
    public void ShellSingleQuote_EscapesEmbeddedQuotes()
    {
        Assert.Equal("'plain'", PostReceiveHook.ShellSingleQuote("plain"));
        Assert.Equal("'a'\"'\"'b'", PostReceiveHook.ShellSingleQuote("a'b"));
    }

    [Fact]
    public void Install_WritesExecutableHook()
    {
        using var tmp = new TempDir();
        var bare = tmp.Path;
        Directory.CreateDirectory(Path.Combine(bare, "hooks"));
        var hook = new PostReceiveHook(new GitClient());
        hook.Install(bare);
        var hookPath = Path.Combine(bare, "hooks", "post-receive");
        Assert.True(System.IO.File.Exists(hookPath));
        Assert.Contains("daemon notify-push", System.IO.File.ReadAllText(hookPath));
        if (!OperatingSystem.IsWindows())
        {
            var mode = System.IO.File.GetUnixFileMode(hookPath);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void RefreshManaged_PreservesCustomHookInstallsMissing()
    {
        using var tmp = new TempDir();
        var bare = tmp.Path;
        var hooksDir = Path.Combine(bare, "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "post-receive");
        var hook = new PostReceiveHook(new GitClient());

        // Missing hook: installed.
        Assert.True(hook.RefreshManaged(bare));
        Assert.True(System.IO.File.Exists(hookPath));

        // Idempotent refresh of the managed hook: no change.
        Assert.False(hook.RefreshManaged(bare));

        // Custom hook: preserved (not overwritten).
        System.IO.File.WriteAllText(hookPath, "#!/bin/sh\necho custom\n");
        Assert.False(hook.RefreshManaged(bare));
        Assert.Contains("echo custom", System.IO.File.ReadAllText(hookPath));
    }

    [Fact]
    public async Task IsolateHooksPath_PinsHooksPathPerWorktree()
    {
        using var tmp = new TempDir();
        var bare = tmp.File("gate.git");
        var git = new GitClient();
        await git.InitBareAsync(bare);
        var hook = new PostReceiveHook(git);

        await hook.IsolateHooksPathAsync(bare);

        var worktreeExt = await git.RunAsync(bare, "config", "--get", "extensions.worktreeConfig");
        Assert.Equal("true", worktreeExt);
        var hooksPath = await git.RunAsync(bare, "config", "--worktree", "--get", "core.hookspath");
        Assert.EndsWith("hooks", hooksPath);

        // Idempotent: a second call must not throw.
        await hook.IsolateHooksPathAsync(bare);
    }
}
