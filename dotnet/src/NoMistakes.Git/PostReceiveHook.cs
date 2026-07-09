namespace NoMistakes.Git;

/// <summary>
/// Manages the no-mistakes post-receive hook on a bare gate repository: script
/// generation, install/refresh, and the per-worktree config isolation that
/// protects the hook from being disabled by pipeline subprocesses. Ports Go's
/// <c>internal/git/hook.go</c>.
/// </summary>
public sealed class PostReceiveHook
{
    private readonly GitClient _git;

    public PostReceiveHook(GitClient git)
    {
        _git = git;
    }

    /// <summary>
    /// Returns the shell script for the post-receive hook. The hook notifies the
    /// daemon via the CLI so it works across platforms, and never blocks the
    /// push: failures are surfaced to stderr and appended to notify-push.log
    /// inside the bare repo.
    /// </summary>
    public static string Script()
    {
        var exe = Environment.ProcessPath ?? "no-mistakes";
        return ScriptFor(exe);
    }

    internal static string ScriptFor(string command)
    {
        return "#!/bin/sh\n" +
            "# no-mistakes post-receive hook\n" +
            "# Notifies the daemon of the push. Non-blocking: post-receive exit code is\n" +
            "# ignored by git, so we never reject the push here. Instead, failures are\n" +
            "# surfaced on stderr (so the pushing client sees them) and appended to\n" +
            "# notify-push.log inside the bare repo for later inspection.\n" +
            "NM_BIN=" + ShellSingleQuote(command) + "\n" +
            "if [ ! -f \"$NM_BIN\" ]; then\n" +
            "  NM_BIN=\"$(command -v no-mistakes 2>/dev/null || echo no-mistakes)\"\n" +
            "fi\n" +
            "# Resolve the bare repo dir explicitly. Git can invoke this hook from a cwd\n" +
            "# whose pwd collapses to \".\" (issue #269), which would pass \"--gate .\" and be\n" +
            "# rejected by the daemon (\"invalid gate path: .\"), so the pipeline never\n" +
            "# starts. git rev-parse --absolute-git-dir queries git directly and always\n" +
            "# yields the true path regardless of cwd/PWD state (Git 2.13+, May 2017); fall\n" +
            "# back to pwd only if git itself is somehow unavailable.\n" +
            "GATE_DIR=$(git rev-parse --absolute-git-dir 2>/dev/null || pwd)\n" +
            "LOG=\"$GATE_DIR/notify-push.log\"\n" +
            "nm_ts() { date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown; }\n" +
            "notify_failed=0\n" +
            "while read oldrev newrev refname; do\n" +
            "\t  set -- --gate \"$GATE_DIR\" \\\n" +
            "\t    --ref \"$refname\" \\\n" +
            "\t    --old \"$oldrev\" \\\n" +
            "\t    --new \"$newrev\"\n" +
            "\t  i=0\n" +
            "\t  while [ \"$i\" -lt \"${GIT_PUSH_OPTION_COUNT:-0}\" ]; do\n" +
            "\t    opt=$(printenv \"GIT_PUSH_OPTION_$i\" 2>/dev/null || :)\n" +
            "\t    set -- \"$@\" --push-option \"$opt\"\n" +
            "\t    i=$((i + 1))\n" +
            "\t  done\n" +
            "\t  out=$(NM_HOOK_HELPER=1 \"$NM_BIN\" daemon notify-push \"$@\" 2>&1)\n" +
            "  status=$?\n" +
            "  if [ $status -ne 0 ]; then\n" +
            "    notify_failed=1\n" +
            "    {\n" +
            "      printf '[%s] notify-push failed for %s (exit %d)\\n' \"$(nm_ts)\" \"$refname\" \"$status\"\n" +
            "      printf '%s\\n\\n' \"$out\"\n" +
            "    } >> \"$LOG\"\n" +
            "    {\n" +
            "      printf 'no-mistakes: notify-push failed for %s (exit %d):\\n' \"$refname\" \"$status\"\n" +
            "      printf '%s\\n' \"$out\"\n" +
            "      printf 'See %s for full history.\\n' \"$LOG\"\n" +
            "    } >&2\n" +
            "  fi\n" +
            "done\n" +
            "\n" +
            "if [ \"$notify_failed\" -eq 0 ]; then\n" +
            "  cat >&2 <<'BANNER'\n" +
            "_  _ ____    _  _ _ ____ ___ ____ _  _ ____ ____\n" +
            "|\\ | |  |    |\\/| | [__   |  |__| |_/  |___ [__\n" +
            "| \\| |__|    |  | | ___]  |  |  | | \\_ |___ ___]\n" +
            "\n" +
            "  * Pipeline started\n" +
            "\n" +
            "  Run no-mistakes to review.\n" +
            "\n" +
            "BANNER\n" +
            "fi\n" +
            "exit 0\n";
    }

    internal static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static bool IsManagedPostReceiveHook(string content) =>
        content.Contains("# no-mistakes post-receive hook", StringComparison.Ordinal)
        && content.Contains("daemon notify-push", StringComparison.Ordinal);

    /// <summary>
    /// Writes the post-receive hook script into the hooks directory of a bare
    /// repo at <paramref name="bareDir"/>.
    /// </summary>
    public void Install(string bareDir)
    {
        var hooksDir = Path.Combine(bareDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        WriteHookFileAtomic(Path.Combine(hooksDir, "post-receive"), Script());
    }

    /// <summary>
    /// Updates an existing no-mistakes-owned hook. Custom hooks are left
    /// untouched; a missing hook is installed. Returns true when the file was
    /// (re)written.
    /// </summary>
    public bool RefreshManaged(string bareDir)
    {
        var hooksDir = Path.Combine(bareDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "post-receive");
        var desired = Script();
        if (System.IO.File.Exists(hookPath))
        {
            var existing = System.IO.File.ReadAllText(hookPath);
            if (existing == desired)
            {
                return false;
            }
            if (!IsManagedPostReceiveHook(existing))
            {
                return false;
            }
        }
        WriteHookFileAtomic(hookPath, desired);
        return true;
    }

    private static void WriteHookFileAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmpPath = Path.Combine(dir, ".post-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.File.WriteAllText(tmpPath, content);
            SetExecutable(tmpPath);
            System.IO.File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath))
            {
                System.IO.File.Delete(tmpPath);
            }
        }
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = System.IO.File.GetUnixFileMode(path);
            System.IO.File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Protects the gate's post-receive hook from being disabled when a pipeline
    /// subprocess runs <c>git config core.hookspath</c> from inside a linked
    /// worktree. Enables extensions.worktreeConfig and pins core.hookspath in the
    /// bare's per-worktree config, then relocates core.bare to per-worktree scope
    /// (required once the extension is on). Best-effort: returns without change
    /// when the installed git lacks <c>--worktree</c>. Idempotent.
    /// </summary>
    public async Task IsolateHooksPathAsync(string bareDir, CancellationToken ct = default)
    {
        try
        {
            await _git.RunAsync(bareDir, new[] { "config", "--worktree", "--get", "core.hookspath" }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex) when (IsWorktreeConfigUnsupported(ex))
        {
            return;
        }
        catch (GitCommandException)
        {
            // key not set yet, or other non-fatal read failure; continue to set.
        }

        await _git.RunAsync(bareDir, new[] { "config", "extensions.worktreeConfig", "true" }, ct).ConfigureAwait(false);

        var hooksDir = Path.GetFullPath(Path.Combine(bareDir, "hooks"));
        try
        {
            await _git.RunAsync(bareDir, new[] { "config", "--worktree", "core.hookspath", hooksDir }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex) when (IsWorktreeConfigUnsupported(ex))
        {
            return;
        }
        await RelocateCoreBareToWorktreeScopeAsync(bareDir, ct).ConfigureAwait(false);
    }

    private async Task RelocateCoreBareToWorktreeScopeAsync(string bareDir, CancellationToken ct)
    {
        try
        {
            await _git.RunAsync(bareDir, new[] { "config", "--worktree", "core.bare", "true" }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex) when (IsWorktreeConfigUnsupported(ex))
        {
            return;
        }
        try
        {
            await _git.RunAsync(bareDir, new[] { "config", "--local", "--unset", "core.bare" }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex) when (ex.ExitCode == 5)
        {
            // exit 5 = key not set: unset is idempotent.
        }
    }

    private static bool IsWorktreeConfigUnsupported(GitCommandException ex)
    {
        var msg = ex.Message;
        return msg.Contains("unknown option", StringComparison.Ordinal)
            && msg.Contains("worktree", StringComparison.Ordinal);
    }
}
