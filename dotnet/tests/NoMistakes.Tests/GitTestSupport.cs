using System.Diagnostics;

namespace NoMistakes.Tests;

/// <summary>
/// Helpers for the git-wrapper tests: run raw git against a real temp repo and
/// seed repos deterministically. Mirrors the Go tests, which create real git
/// repositories in temp dirs rather than mocking.
/// </summary>
internal static class GitTestSupport
{
    /// <summary>
    /// Runs git directly (bypassing the wrapper) and returns trimmed stdout.
    /// Fails the calling logic via an exception when git exits non-zero, so
    /// test setup errors are loud.
    /// </summary>
    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // Deterministic identity/branch so tests do not depend on ambient git config.
        psi.Environment["GIT_AUTHOR_NAME"] = "test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@test.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@test.com";

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed ({p.ExitCode}): {stderr.Trim()}");
        }
        return stdout.Trim();
    }

    /// <summary>
    /// Initializes a repo at <paramref name="dir"/> on branch main with a
    /// deterministic user identity, and returns the dir.
    /// </summary>
    public static string InitRepo(string dir)
    {
        Git(dir, "init", "-q");
        Git(dir, "config", "user.name", "test");
        Git(dir, "config", "user.email", "test@test.com");
        Git(dir, "checkout", "-q", "-b", "main");
        return dir;
    }

    /// <summary>Writes a file and commits it, returning the new HEAD SHA.</summary>
    public static string WriteAndCommit(string dir, string name, string content, string message)
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name), content);
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", message);
        return Git(dir, "rev-parse", "HEAD");
    }
}
