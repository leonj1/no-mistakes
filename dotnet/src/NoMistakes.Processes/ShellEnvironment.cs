using System.Runtime.InteropServices;

namespace NoMistakes.Processes;

/// <summary>
/// Resolves the login shell's environment so spawned tools inherit the same
/// PATH a human would in an interactive terminal (version-manager dirs such as
/// nvm/fnm/volta, Homebrew, language package managers). Ports Go's
/// <c>internal/shellenv</c> environment half.
///
/// A successful shell probe is cached for the process lifetime; a degraded
/// fallback (probe failed or returned nothing) is deliberately NOT cached, so a
/// single bad startup cannot poison every later resolution (#143).
/// </summary>
public sealed class ShellEnvironment
{
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, (int ExitCode, string Stdout)> _runShell;
    private readonly Func<string, string?> _getEnv;
    private readonly string? _homeOverride;

    private readonly object _cacheLock = new();
    private IReadOnlyList<string>? _cached;

    /// <summary>Bounds the one-time login-shell probe. Deliberately forgiving (#143).</summary>
    public static readonly TimeSpan ShellCommandTimeout = TimeSpan.FromSeconds(30);

    public ShellEnvironment()
        : this(DefaultRunShell, Environment.GetEnvironmentVariable, null)
    {
    }

    internal ShellEnvironment(
        Func<string, IReadOnlyList<string>, TimeSpan, (int ExitCode, string Stdout)> runShell,
        Func<string, string?> getEnv,
        string? homeOverride)
    {
        _runShell = runShell;
        _getEnv = getEnv;
        _homeOverride = homeOverride;
    }

    /// <summary>
    /// Returns the login shell path: $SHELL when set, else the OS user database
    /// (getent on Linux), else "bash".
    /// </summary>
    public string LoginShell()
    {
        var shell = _getEnv("SHELL");
        if (!string.IsNullOrWhiteSpace(shell))
        {
            return shell;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var fromGetent = ShellFromGetent();
            if (!string.IsNullOrEmpty(fromGetent))
            {
                return fromGetent;
            }
        }
        return "bash";
    }

    /// <summary>Reports whether the shell supports an interactive login probe.</summary>
    public static bool SupportsInteractive(string shell)
    {
        var baseName = Path.GetFileName(shell);
        return baseName is "bash" or "zsh";
    }

    /// <summary>
    /// Resolves the environment, caching only a successful shell probe. Returns a
    /// copy so callers cannot mutate the cache.
    /// </summary>
    public IReadOnlyList<string> Resolve()
    {
        lock (_cacheLock)
        {
            if (_cached != null)
            {
                return new List<string>(_cached);
            }
        }

        var (resolved, fromShell) = ResolveUncached();

        if (fromShell)
        {
            lock (_cacheLock)
            {
                _cached ??= new List<string>(resolved);
            }
        }
        return new List<string>(resolved);
    }

    /// <summary>Applies the resolved environment onto the current process.</summary>
    public void ApplyToProcess()
    {
        foreach (var entry in Resolve())
        {
            var idx = entry.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }
            var key = entry[..idx];
            var value = entry[(idx + 1)..];
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private (IReadOnlyList<string> Env, bool FromShell) ResolveUncached()
    {
        var shell = LoginShell();
        var args = SupportsInteractive(shell)
            ? new[] { "-l", "-i", "-c", "env -0" }
            : new[] { "-l", "-c", "env -0" };

        (int ExitCode, string Stdout) result;
        try
        {
            result = _runShell(shell, args, ShellCommandTimeout);
        }
        catch
        {
            return (AugmentPath(EnsureShellEntry(CurrentEnv(), shell)), false);
        }

        if (result.ExitCode != 0)
        {
            return (AugmentPath(EnsureShellEntry(CurrentEnv(), shell)), false);
        }

        var parsed = ParseEnvOutput(result.Stdout);
        if (parsed.Count == 0)
        {
            return (AugmentPath(EnsureShellEntry(CurrentEnv(), shell)), false);
        }
        return (AugmentPath(EnsureShellEntry(parsed, shell)), true);
    }

    private List<string> CurrentEnv()
    {
        var env = new List<string>();
        foreach (System.Collections.DictionaryEntry kvp in Environment.GetEnvironmentVariables())
        {
            env.Add($"{kvp.Key}={kvp.Value}");
        }
        return env;
    }

    /// <summary>
    /// Common binary install locations that should be on PATH. Non-existent dirs
    /// are included unchanged (PATH lookup skips missing entries), matching Go.
    /// </summary>
    public IReadOnlyList<string> WellKnownBinDirs() => WellKnownBinDirsForHome(HomeDir());

    public static IReadOnlyList<string> WellKnownBinDirsForHome(string home)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(home))
        {
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(home, "go", "bin"));
            dirs.Add(Path.Combine(home, ".cargo", "bin"));
            dirs.Add(Path.Combine(home, "bin"));
        }
        dirs.AddRange(new[]
        {
            "/opt/homebrew/bin",
            "/opt/homebrew/sbin",
            "/usr/local/bin",
            "/usr/local/sbin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
        });
        return dirs;
    }

    /// <summary>
    /// Merges <see cref="WellKnownBinDirs"/> into the PATH entry of
    /// <paramref name="env"/>, preserving existing order (user PATH wins) and
    /// appending only dirs not already present. Synthesizes PATH if absent.
    /// </summary>
    public IReadOnlyList<string> AugmentPath(IReadOnlyList<string> env)
    {
        const char sep = ':';
        var result = new List<string>(env);
        var pathIdx = -1;
        var existing = new List<string>();
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].StartsWith("PATH=", StringComparison.Ordinal))
            {
                pathIdx = i;
                var raw = result[i]["PATH=".Length..];
                if (raw.Length > 0)
                {
                    existing.AddRange(raw.Split(sep));
                }
                break;
            }
        }
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        foreach (var d in WellKnownBinDirs())
        {
            if (seen.Add(d))
            {
                existing.Add(d);
            }
        }
        var merged = "PATH=" + string.Join(sep, existing);
        if (pathIdx >= 0)
        {
            result[pathIdx] = merged;
        }
        else
        {
            result.Add(merged);
        }
        return result;
    }

    /// <summary>
    /// Parses NUL-delimited <c>env -0</c> output into KEY=VALUE entries, skipping
    /// shell noise before the first valid key (a login/interactive shell can emit
    /// banners/MOTD before the env dump).
    /// </summary>
    public static IReadOnlyList<string> ParseEnvOutput(string output)
    {
        var env = new List<string>();
        foreach (var part in output.Split('\0'))
        {
            if (TryParseEnvEntry(part, out var entry))
            {
                env.Add(entry);
            }
        }
        return env;
    }

    private static bool TryParseEnvEntry(string part, out string entry)
    {
        entry = string.Empty;
        if (part.Length == 0)
        {
            return false;
        }
        var candidateStarts = new List<int> { 0 };
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] is '\n' or '\r')
            {
                candidateStarts.Add(i + 1);
            }
        }
        foreach (var start in candidateStarts)
        {
            var candidate = part[start..].TrimStart('\r', '\n');
            if (candidate.Length == 0)
            {
                continue;
            }
            var idx = candidate.IndexOf('=');
            if (idx > 0 && ValidEnvKey(candidate[..idx]))
            {
                entry = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool ValidEnvKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }
        for (var i = 0; i < key.Length; i++)
        {
            var r = key[i];
            var isAlphaOrUnderscore = (r >= 'A' && r <= 'Z') || (r >= 'a' && r <= 'z') || r == '_';
            if (i == 0)
            {
                if (!isAlphaOrUnderscore)
                {
                    return false;
                }
                continue;
            }
            if (!isAlphaOrUnderscore && !(r >= '0' && r <= '9'))
            {
                return false;
            }
        }
        return true;
    }

    private static List<string> EnsureShellEntry(IReadOnlyList<string> env, string shell)
    {
        var result = new List<string>(env);
        foreach (var entry in result)
        {
            if (entry.StartsWith("SHELL=", StringComparison.Ordinal))
            {
                return result;
            }
        }
        result.Add("SHELL=" + shell);
        return result;
    }

    private string HomeDir()
    {
        if (!string.IsNullOrWhiteSpace(_homeOverride))
        {
            return _homeOverride!;
        }
        var home = _getEnv("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home!;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string ShellFromGetent()
    {
        var username = CurrentUsername();
        if (string.IsNullOrEmpty(username))
        {
            return string.Empty;
        }
        try
        {
            var (exitCode, stdout) = _runShell("getent", new[] { "passwd", username }, ShellCommandTimeout);
            if (exitCode != 0)
            {
                return string.Empty;
            }
            var line = stdout.Trim();
            if (line.Length == 0)
            {
                return string.Empty;
            }
            var parts = line.Split(':');
            return parts.Length == 0 ? string.Empty : parts[^1].Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private string CurrentUsername()
    {
        var user = _getEnv("USER");
        if (!string.IsNullOrWhiteSpace(user))
        {
            return user!;
        }
        return Environment.UserName;
    }

    private static (int ExitCode, string Stdout) DefaultRunShell(string name, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var spec = new ShellCommandSpec(name, args.ToArray())
        {
            WaitDelay = TimeSpan.FromMilliseconds(100),
        };
        using var cts = new CancellationTokenSource(timeout);
        var result = new ShellCommand(spec).OutputAsync(cts.Token).GetAwaiter().GetResult();
        return (result.ExitCode, result.Stdout);
    }
}
