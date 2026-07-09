using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NoMistakes.Scm.SafeUrl;

namespace NoMistakes.Git;

/// <summary>
/// Thrown when a git subprocess exits non-zero. Carries the redacted command
/// and stderr so callers can inspect or surface the failure. Mirrors the error
/// string shape produced by Go's <c>git.Run</c>: "git &lt;args&gt;: &lt;exit&gt;: &lt;stderr&gt;".
/// </summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>
/// Wraps the local <c>git</c> executable. Ports Go's <c>internal/git</c>: a
/// command runner plus repository-model helpers (discovery, remotes, worktrees,
/// SHA/ref resolution, diff/log, push/fetch) with explicit handling for bare
/// gate repositories under <c>safe.bareRepository=explicit</c>.
///
/// The Go package exposes free functions taking <c>(ctx, dir, ...)</c>. Here the
/// equivalent is an instance so tests and callers can inject dependencies, but
/// every operation still takes the working <c>dir</c> per call, matching the Go
/// signatures that remain the compatibility oracle.
/// </summary>
public sealed class GitClient
{
    /// <summary>
    /// The well-known SHA of git's empty tree. Used as a diff base when there
    /// is no prior commit.
    /// </summary>
    public const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private const string ZeroSha = "0000000000000000000000000000000000000000";

    private readonly string _gitExecutable;

    public GitClient(string gitExecutable = "git")
    {
        _gitExecutable = gitExecutable;
    }

    /// <summary>
    /// Reports whether a SHA is the null/zero ref git uses for new or deleted
    /// branches (40 zeros).
    /// </summary>
    public static bool IsZeroSha(string sha) => sha == ZeroSha;

    /// <summary>
    /// Runs a git command in <paramref name="dir"/> and returns trimmed stdout.
    /// Throws <see cref="GitCommandException"/> with the redacted command and
    /// stderr on failure.
    ///
    /// When <paramref name="dir"/> is itself a bare repository (a gate repo),
    /// the repo is named explicitly via <c>--git-dir</c> instead of relying on
    /// cwd-based discovery, which <c>safe.bareRepository=explicit</c> forbids.
    /// Agent harnesses and hardened CI inject that setting, so gate operations
    /// must never depend on discovering a bare repo from the working directory.
    /// </summary>
    public async Task<string> RunAsync(string dir, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var effectiveArgs = new List<string>(args.Count + 1);
        if (IsBareGitDir(dir))
        {
            effectiveArgs.Add("--git-dir=" + dir);
        }
        effectiveArgs.AddRange(args);

        var (exitCode, stdout, stderr) = await ExecAsync(dir, effectiveArgs, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var redactedArgs = Redactor.RedactText(string.Join(" ", effectiveArgs));
            var redactedStderr = Redactor.RedactText(stderr.Trim());
            throw new GitCommandException(
                $"git {redactedArgs}: exit status {exitCode}: {redactedStderr}", exitCode);
        }
        return stdout.Trim();
    }

    /// <summary>Convenience overload taking a params array of arguments.</summary>
    public Task<string> RunAsync(string dir, params string[] args) => RunAsync(dir, args, CancellationToken.None);

    /// <summary>
    /// Builds the environment for a git subprocess forced into non-interactive
    /// mode. Without these overrides, git can open $EDITOR (e.g. during commit
    /// or rebase --continue) or block on a credential prompt; in a headless
    /// process that hangs. Overrides win over ambient values.
    ///
    /// Pass the same directory used as the working directory (or null/empty when
    /// unset): a PWD absolute path is injected on non-Windows platforms so a
    /// symlinked working directory (e.g. /tmp vs /private/tmp on macOS) is
    /// preserved for descendants that trust PWD.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NonInteractiveEnv(string? dir)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_EDITOR"] = "true",
            ["GIT_SEQUENCE_EDITOR"] = "true",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        if (!string.IsNullOrEmpty(dir) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                env["PWD"] = Path.GetFullPath(dir);
            }
            catch
            {
                // Leave PWD unset when the path cannot be absolutized.
            }
        }
        return env;
    }

    // --- Repository discovery -------------------------------------------------

    /// <summary>
    /// Reports whether <paramref name="dir"/> is itself a git directory (a bare
    /// repo), as opposed to a working tree or linked worktree (which carry a
    /// .git entry and keep normal discovery). Mirrors git's own heuristic: a
    /// HEAD file plus an objects directory, and no .git entry.
    /// </summary>
    public static bool IsBareGitDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }
        if (Path.Exists(Path.Combine(dir, ".git")))
        {
            return false;
        }
        var headPath = Path.Combine(dir, "HEAD");
        if (!System.IO.File.Exists(headPath))
        {
            return false;
        }
        return Directory.Exists(Path.Combine(dir, "objects"));
    }

    /// <summary>
    /// Walks up from <paramref name="path"/> to the git repository root,
    /// resolving symlinks for consistency (e.g. /tmp -&gt; /private/tmp on macOS).
    /// </summary>
    public async Task<string> FindGitRootAsync(string path, CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        var (exitCode, stdout, _) = await ExecAsync(abs, new[] { "rev-parse", "--show-toplevel" }, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new GitCommandException($"not a git repository: {abs}", exitCode);
        }
        return ResolveSymlinks(stdout.Trim());
    }

    /// <summary>
    /// Returns the root of the main working tree. For a regular repo this equals
    /// <see cref="FindGitRootAsync"/>; for a linked worktree it resolves back to
    /// the main repository root via git's common dir.
    /// </summary>
    public async Task<string> FindMainRepoRootAsync(string path, CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        var (exitCode, stdout, _) = await ExecAsync(abs, new[] { "rev-parse", "--git-common-dir" }, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new GitCommandException($"not a git repository: {abs}", exitCode);
        }
        var commonDir = stdout.Trim();
        if (!Path.IsPathRooted(commonDir))
        {
            commonDir = Path.Combine(abs, commonDir);
        }
        // commonDir is the .git directory; its parent is the repo root.
        var root = Path.GetDirectoryName(Path.GetFullPath(commonDir)) ?? commonDir;
        return ResolveSymlinks(root);
    }

    // --- Remotes --------------------------------------------------------------

    public Task InitBareAsync(string path, CancellationToken ct = default) =>
        RunAsync(WorkingDirFor(path), new[] { "init", "--bare", path }, ct);

    public Task AddRemoteAsync(string dir, string name, string url, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "remote", "add", name, url }, ct);

    /// <summary>
    /// Sets the named remote to <paramref name="url"/>, adding it when absent and
    /// updating its URL when present. Idempotent.
    /// </summary>
    public async Task EnsureRemoteAsync(string dir, string name, string url, CancellationToken ct = default)
    {
        try
        {
            await GetRemoteUrlAsync(dir, name, ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            await AddRemoteAsync(dir, name, url, ct).ConfigureAwait(false);
            return;
        }
        await RunAsync(dir, new[] { "remote", "set-url", name, url }, ct).ConfigureAwait(false);
    }

    public Task RemoveRemoteAsync(string dir, string name, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "remote", "remove", name }, ct);

    public Task<string> GetRemoteUrlAsync(string dir, string name, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "remote", "get-url", name }, ct);

    /// <summary>
    /// Returns the literal remote URL from git config, without applying
    /// url.*.insteadOf rewrites.
    /// </summary>
    public Task<string> GetConfiguredRemoteUrlAsync(string dir, string name, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "config", "--get", "remote." + name + ".url" }, ct);

    // --- Diff / log -----------------------------------------------------------

    public Task<string> DiffAsync(string dir, string @base, string head, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "diff", @base + ".." + head }, ct);

    /// <summary>
    /// Returns the files changed between base and head, with empty entries
    /// removed.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiffNameOnlyAsync(string dir, string @base, string head, CancellationToken ct = default)
    {
        var output = await RunAsync(dir, new[] { "diff", "--name-only", @base + ".." + head }, ct).ConfigureAwait(false);
        return SplitNonEmptyLines(output);
    }

    public Task<string> DiffHeadAsync(string dir, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "diff", "HEAD" }, ct);

    public Task<string> LogAsync(string dir, string @base, string head, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "log", "--oneline", @base + ".." + head }, ct);

    /// <summary>Returns the committer timestamp for a SHA in UTC.</summary>
    public async Task<DateTimeOffset> CommitTimeAsync(string dir, string sha, CancellationToken ct = default)
    {
        var output = await RunAsync(dir, new[] { "show", "-s", "--format=%ct", sha }, ct).ConfigureAwait(false);
        if (!long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
        {
            throw new GitCommandException($"parse commit time \"{output}\"", 0);
        }
        return DateTimeOffset.FromUnixTimeSeconds(secs).ToUniversalTime();
    }

    public Task<string> CommitAuthorEmailAsync(string dir, string sha, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "show", "-s", "--format=%ae", sha }, ct);

    // --- HEAD / branch state --------------------------------------------------

    public Task<string> HeadShaAsync(string dir, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "rev-parse", "HEAD" }, ct);

    public Task<string> CurrentBranchAsync(string dir, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct);

    /// <summary>
    /// Reports whether the working tree is in a detached-HEAD state. Uses
    /// <c>git symbolic-ref -q HEAD</c>, which exits 1 when HEAD is not a
    /// symbolic ref (detached).
    /// </summary>
    public async Task<bool> IsDetachedHeadAsync(string dir, CancellationToken ct = default)
    {
        var (exitCode, _, _) = await ExecAsync(dir, new[] { "symbolic-ref", "-q", "HEAD" }, ct).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return false;
        }
        if (exitCode == 1)
        {
            return true;
        }
        throw new GitCommandException($"git symbolic-ref: exit status {exitCode}", exitCode);
    }

    /// <summary>
    /// Queries a remote for its default branch via <c>ls-remote --symref</c>.
    /// Falls back to "main" if detection fails (empty/unreachable remote).
    /// </summary>
    public async Task<string> DefaultBranchAsync(string dir, string remote, CancellationToken ct = default)
    {
        string output;
        try
        {
            output = await RunAsync(dir, new[] { "ls-remote", "--symref", remote, "HEAD" }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            return "main";
        }
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? parts[1]["refs/heads/".Length..]
                        : parts[1];
                }
            }
        }
        return "main";
    }

    // --- Fetch / push ---------------------------------------------------------

    /// <summary>
    /// Fetches a single branch into a remote-tracking ref, force-updating (+) so
    /// non-fast-forward updates (e.g. after a remote force push) are accepted.
    /// </summary>
    public Task FetchRemoteBranchAsync(string dir, string remote, string branch, CancellationToken ct = default)
    {
        var refspec = $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}";
        return RunAsync(dir, new[] { "fetch", "--no-tags", remote, refspec }, ct);
    }

    public Task FetchRemoteBranchToRefAsync(string dir, string remote, string branch, string localRef, CancellationToken ct = default)
    {
        var refspec = $"+refs/heads/{branch}:{localRef}";
        return RunAsync(dir, new[] { "fetch", "--no-tags", remote, refspec }, ct);
    }

    /// <summary>
    /// Pushes a ref to a remote. When <paramref name="forceWithLease"/> is true,
    /// uses <c>--force-with-lease</c> anchored to <paramref name="expectedSha"/>
    /// when non-empty.
    /// </summary>
    public Task PushAsync(string dir, string remote, string @ref, string expectedSha, bool forceWithLease, CancellationToken ct = default) =>
        PushWithOptionsAsync(dir, remote, @ref, expectedSha, forceWithLease, null, ct);

    public Task PushWithOptionsAsync(string dir, string remote, string @ref, string expectedSha, bool forceWithLease, IReadOnlyList<string>? pushOptions, CancellationToken ct = default)
    {
        var args = new List<string> { "push" };
        if (pushOptions != null)
        {
            foreach (var option in pushOptions)
            {
                args.Add("-o");
                args.Add(option);
            }
        }
        args.Add(remote);
        if (forceWithLease)
        {
            args.Add(expectedSha.Length > 0
                ? $"--force-with-lease={@ref}:{expectedSha}"
                : "--force-with-lease");
        }
        args.Add("HEAD:" + @ref);
        return RunAsync(dir, args, ct);
    }

    /// <summary>
    /// Returns the SHA of a ref on a remote, or an empty string when the ref
    /// does not exist.
    /// </summary>
    public async Task<string> LsRemoteAsync(string dir, string remote, string @ref, CancellationToken ct = default)
    {
        var output = await RunAsync(dir, new[] { "ls-remote", remote, @ref }, ct).ConfigureAwait(false);
        if (output.Length == 0)
        {
            return string.Empty;
        }
        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 1 ? string.Empty : parts[0];
    }

    // --- Working-tree state / mutation ---------------------------------------

    /// <summary>
    /// Reports whether the working tree or index differs from HEAD (equivalent
    /// to a non-empty <c>git status --porcelain</c>).
    /// </summary>
    public async Task<bool> HasUncommittedChangesAsync(string dir, CancellationToken ct = default)
    {
        var output = await RunAsync(dir, new[] { "status", "--porcelain" }, ct).ConfigureAwait(false);
        return output.Length != 0;
    }

    /// <summary>Creates a new branch and switches to it. Fails if it exists.</summary>
    public Task CreateBranchAsync(string dir, string name, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "checkout", "-b", name }, ct);

    /// <summary>
    /// Stages every change and creates one commit. Fails if there is nothing to
    /// commit.
    /// </summary>
    public async Task CommitAllAsync(string dir, string message, CancellationToken ct = default)
    {
        await RunAsync(dir, new[] { "add", "-A" }, ct).ConfigureAwait(false);
        if (!await HasUncommittedChangesAsync(dir, ct).ConfigureAwait(false))
        {
            throw new GitCommandException("no changes to commit", 0);
        }
        await RunAsync(dir, new[] { "commit", "-m", message }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies local user.name and user.email from <paramref name="srcDir"/> into
    /// <paramref name="dstDir"/>, preferring per-worktree scope so concurrent
    /// runs on a shared bare repo do not race on the shared config lock. Missing
    /// source values are ignored; older git without <c>--worktree</c> falls back
    /// to <c>--local</c>.
    /// </summary>
    public async Task CopyLocalUserIdentityAsync(string srcDir, string dstDir, CancellationToken ct = default)
    {
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var value = await RunAsync(srcDir, new[] { "config", "--local", "--get", "--default", "", key }, ct).ConfigureAwait(false);
            if (value.Length == 0)
            {
                continue;
            }
            try
            {
                await RunAsync(dstDir, new[] { "config", "--worktree", key, value }, ct).ConfigureAwait(false);
            }
            catch (GitCommandException ex) when (IsWorktreeConfigWriteUnavailable(ex))
            {
                await RunAsync(dstDir, new[] { "config", "--local", key, value }, ct).ConfigureAwait(false);
            }
        }
    }

    // --- Worktrees ------------------------------------------------------------

    public Task WorktreeAddAsync(string repoDir, string wtPath, string sha, CancellationToken ct = default) =>
        RunAsync(repoDir, new[] { "worktree", "add", "--detach", wtPath, sha }, ct);

    public Task WorktreeRemoveAsync(string repoDir, string wtPath, CancellationToken ct = default) =>
        RunAsync(repoDir, new[] { "worktree", "remove", "--force", wtPath }, ct);

    // --- Ref resolution -------------------------------------------------------

    /// <summary>
    /// Resolves a ref to an exact commit SHA via
    /// <c>rev-parse --verify --quiet &lt;ref&gt;^{commit}</c>. Throws when the ref
    /// does not resolve to a commit.
    /// </summary>
    public async Task<string> ResolveRefAsync(string dir, string @ref, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(dir, new[] { "rev-parse", "--verify", "--quiet", @ref + "^{commit}" }, ct).ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            throw new GitCommandException($"resolve ref {@ref}: {ex.Message}", ex.ExitCode);
        }
    }

    /// <summary>
    /// Reports whether a ref resolves to a commit. A missing ref is a clean
    /// false, not an error (uses <c>rev-parse --verify --quiet</c>, exit 1).
    /// </summary>
    public async Task<bool> RefExistsAsync(string dir, string @ref, CancellationToken ct = default)
    {
        var (exitCode, _, _) = await ExecAsync(dir, new[] { "rev-parse", "--verify", "--quiet", @ref + "^{commit}" }, ct).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return true;
        }
        if (exitCode == 1)
        {
            return false;
        }
        throw new GitCommandException($"git rev-parse {@ref}: exit status {exitCode}", exitCode);
    }

    /// <summary>
    /// Returns the content of <paramref name="path"/> as stored at
    /// <paramref name="ref"/> (e.g. "HEAD", "origin/main", a SHA) via
    /// <c>git show &lt;ref&gt;:&lt;path&gt;</c>.
    /// </summary>
    public Task<string> ShowFileAsync(string dir, string @ref, string path, CancellationToken ct = default) =>
        RunAsync(dir, new[] { "show", $"{@ref}:{path}" }, ct);

    // --- Internals ------------------------------------------------------------

    private static bool IsWorktreeConfigWriteUnavailable(GitCommandException ex)
    {
        var msg = ex.Message;
        return (msg.Contains("unknown option", StringComparison.Ordinal) && msg.Contains("worktree", StringComparison.Ordinal))
            || msg.Contains("worktreeConfig", StringComparison.Ordinal);
    }

    private static string WorkingDirFor(string path)
    {
        // git init --bare <path> does not need to run inside a repo; use the
        // parent dir (or cwd) as the working directory so exec has a valid cwd.
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent;
    }

    private static string ResolveSymlinks(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? info.FullName;
        }
        catch
        {
            return path;
        }
    }

    private static IReadOnlyList<string> SplitNonEmptyLines(string output)
    {
        var result = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                result.Add(trimmed);
            }
        }
        return result;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string? dir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (!string.IsNullOrEmpty(dir))
        {
            psi.WorkingDirectory = dir;
        }

        // Non-interactive git environment. Inherit the ambient environment then
        // apply overrides last so they win, mirroring Go's NonInteractiveEnv.
        foreach (var kvp in NonInteractiveEnv(dir))
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
