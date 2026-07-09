using NoMistakes.Processes;
using Xunit;

namespace NoMistakes.Tests;

public class ShellEnvironmentTests
{
    // A fake shell runner returning a fixed env-0 dump, so env resolution is
    // deterministic and does not depend on the test host's login shell.
    private static ShellEnvironment WithShellOutput(
        string envDump, int exitCode = 0, string? shellEnv = "/bin/bash",
        string? home = "/home/tester")
    {
        return new ShellEnvironment(
            runShell: (name, args, timeout) => (exitCode, envDump),
            getEnv: key => key switch
            {
                "SHELL" => shellEnv,
                "HOME" => home,
                "USER" => "tester",
                _ => null,
            },
            homeOverride: home);
    }

    [Fact]
    public void LoginShell_UsesShellEnvVar()
    {
        var env = new ShellEnvironment(
            runShell: (_, _, _) => (0, ""),
            getEnv: k => k == "SHELL" ? "/usr/bin/zsh" : null,
            homeOverride: "/home/x");
        Assert.Equal("/usr/bin/zsh", env.LoginShell());
    }

    [Fact]
    public void SupportsInteractive_OnlyBashAndZsh()
    {
        Assert.True(ShellEnvironment.SupportsInteractive("/bin/bash"));
        Assert.True(ShellEnvironment.SupportsInteractive("/usr/bin/zsh"));
        Assert.False(ShellEnvironment.SupportsInteractive("/bin/sh"));
        Assert.False(ShellEnvironment.SupportsInteractive("/bin/fish"));
    }

    [Fact]
    public void Resolve_CapturesEnvAndAugmentsPath()
    {
        var dump = "PATH=/usr/bin\0FOO=bar\0";
        var env = WithShellOutput(dump);
        var resolved = env.Resolve();

        Assert.Contains("FOO=bar", resolved);
        var path = resolved.Single(e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Contains("/usr/bin", path);
        Assert.Contains("/usr/local/bin", path); // appended from well-known dirs
    }

    [Fact]
    public void Resolve_DoesNotDuplicatePathEntries()
    {
        var dump = "PATH=/usr/local/bin:/usr/bin\0";
        var env = WithShellOutput(dump);
        var path = env.Resolve().Single(e => e.StartsWith("PATH=", StringComparison.Ordinal));
        var dirs = path["PATH=".Length..].Split(':');
        Assert.Single(dirs, d => d == "/usr/local/bin");
    }

    [Fact]
    public void Resolve_SynthesizesPathWhenMissing()
    {
        var dump = "FOO=bar\0";
        var env = WithShellOutput(dump);
        var path = env.Resolve().Single(e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Contains("/usr/local/bin", path);
    }

    [Fact]
    public void Resolve_FallbackOnShellFailureNotCached()
    {
        var callCount = 0;
        var env = new ShellEnvironment(
            runShell: (name, args, timeout) =>
            {
                callCount++;
                return (1, ""); // non-zero: degraded fallback, must NOT cache
            },
            getEnv: k => k switch { "SHELL" => "/bin/bash", "HOME" => "/home/x", _ => null },
            homeOverride: "/home/x");

        _ = env.Resolve();
        _ = env.Resolve();
        // A degraded fallback is never cached, so the shell is probed each time.
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Resolve_CachesSuccessfulResolution()
    {
        var callCount = 0;
        var env = new ShellEnvironment(
            runShell: (name, args, timeout) =>
            {
                callCount++;
                return (0, "PATH=/usr/bin\0");
            },
            getEnv: k => k switch { "SHELL" => "/bin/bash", "HOME" => "/home/x", _ => null },
            homeOverride: "/home/x");

        _ = env.Resolve();
        _ = env.Resolve();
        Assert.Equal(1, callCount); // successful resolution cached
    }

    [Fact]
    public void ParseEnvOutput_IgnoresNoiseBeforeEnv()
    {
        // A login/interactive shell can emit a banner line before the env dump.
        var dump = "Welcome to the machine\nPATH=/usr/bin\0FOO=bar\0";
        var parsed = ShellEnvironment.ParseEnvOutput(dump);
        Assert.Contains("PATH=/usr/bin", parsed);
        Assert.Contains("FOO=bar", parsed);
        Assert.DoesNotContain(parsed, e => e.StartsWith("Welcome", StringComparison.Ordinal));
    }

    [Fact]
    public void WellKnownBinDirsForHome_IncludesUserAndSystemDirs()
    {
        var dirs = ShellEnvironment.WellKnownBinDirsForHome("/home/tester");
        Assert.Contains("/home/tester/.local/bin", dirs);
        Assert.Contains("/home/tester/go/bin", dirs);
        Assert.Contains("/usr/local/bin", dirs);
        Assert.Contains("/usr/bin", dirs);
    }
}
