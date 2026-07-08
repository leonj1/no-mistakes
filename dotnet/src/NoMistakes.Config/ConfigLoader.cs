using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NoMistakes.Config;

/// <summary>
/// Loads and merges no-mistakes configuration. Ported from Go's internal/config
/// package. Global config is parsed strictly (unknown top-level fields are an
/// error, mirroring yaml.v3 KnownFields(true)); repo config is parsed leniently.
/// </summary>
public static class ConfigLoader
{
    /// <summary>Template written when no global config file exists.</summary>
    public const string DefaultConfigYaml = @"# no-mistakes global configuration

# Agent to use for code generation. This may also be an ordered fallback list,
# for example: agent: [codex, claude]
# Options: auto, claude, codex, pi, copilot, droid, acp:<target>
# ""auto"" detects the first available native agent on your system
# Use acp:<target> to run an optional user-installed acpx target, for example acp:gemini
agent: auto

# Optional path to the user-installed acpx binary for acp:<target> agents
# acpx_path: acpx

# Optional ACP target command overrides for acp:<target> agents
# acp_registry_overrides:
#   local-gemini: node /opt/mock-acp-agent.mjs

# Maximum time the CI monitor babysits an open PR with no base-branch movement
# before giving up. The monitor watches CI and auto-rebases when the base branch
# advances; each base advance re-arms this timer, so an actively-updated green PR
# keeps its monitor. Set to ""unlimited"", ""none"", ""off"", ""never"", or any
# non-positive duration to monitor until the PR is merged, closed, or the run is
# aborted with: no-mistakes axi abort --run <id>
ci_timeout: ""168h""

# Log level for daemon output
# Options: debug, info, warn, error
log_level: info

# Override native agent binary paths (optional)
# agent_path_override:
#   claude: /usr/local/bin/claude
#   codex: /opt/codex
#   droid: /usr/local/bin/droid

# Extra native agent CLI flags (optional, global only)
# agent_args_override:
#   codex:
#     - -m
#     - gpt-5.4
#
# Maximum follow-up auto-fix attempts per step (0 = disabled after the initial pass)
# Document fixes are attempted during the initial document pass.
auto_fix:
  rebase: 3
  lint: 3
  test: 3
  review: 0
  document: 3
  ci: 3

# User-intent extraction. When you push a branch, no-mistakes can read recent
# transcripts from your local agent (Claude Code, Codex, Pi, Copilot CLI),
# pick the session that produced the change, summarize the user
# intent, and feed it to review, test, document, lint, and PR agents so they
# understand what you were trying to do - not just the diff.
intent:
  enabled: true
  threshold: 0.2
  slack_days: 3
  # disabled_readers: [codex]

# Test-step evidence artifacts (screenshots, recordings, logs the test step
# gathers to demonstrate the change works). By default they are kept in a
# temporary directory and referenced by local path. Opt in to store_in_repo to
# commit them into the repo under a readable, branch-named directory so they are
# pushed and render directly on the PR.
# test:
#   evidence:
#     store_in_repo: true
#     dir: .no-mistakes/evidence
";

    private static readonly HashSet<string> KnownGlobalFields = new(StringComparer.Ordinal)
    {
        "agent",
        "acpx_path",
        "acp_registry_overrides",
        "agent_path_override",
        "agent_args_override",
        "ci_timeout",
        "babysit_timeout",
        "log_level",
        "auto_fix",
        "intent",
        "test",
    };

    private static readonly HashSet<string> AgentArgsOverrideAgents = new(StringComparer.Ordinal)
    {
        AgentNames.Claude,
        AgentNames.Codex,
        AgentNames.Pi,
        AgentNames.Copilot,
        AgentNames.Droid,
    };

    // Flags no-mistakes manages internally, which users cannot override through
    // agent_args_override. Matched by bare form ("--color") and "--color=value".
    private static readonly Dictionary<string, HashSet<string>> ReservedAgentArgs = new(StringComparer.Ordinal)
    {
        [AgentNames.Claude] = new(StringComparer.Ordinal) { "-p", "--print", "--verbose", "--output-format", "--json-schema" },
        [AgentNames.Codex] = new(StringComparer.Ordinal) { "exec", "--json", "--color" },
        [AgentNames.Pi] = new(StringComparer.Ordinal) { "--mode", "--no-session" },
        [AgentNames.Copilot] = new(StringComparer.Ordinal) { "-p", "--prompt", "--output-format", "--no-color" },
        [AgentNames.Droid] = new(StringComparer.Ordinal)
        {
            "exec", "-o", "--output-format", "--input-format", "-f", "--file", "--cwd", "-w", "--worktree", "--worktree-dir",
        },
    };

    /// <summary>
    /// Writes the default config file at path if it does not already exist.
    /// Failures are silently ignored (best-effort), matching the Go behavior.
    /// </summary>
    public static void EnsureDefaultGlobalConfig(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, DefaultConfigYaml);
        }
        catch
        {
            // Best-effort: mirror Go's debug-log-and-ignore behavior.
        }
    }

    /// <summary>Reads global config from path. Returns defaults if the file doesn't exist.</summary>
    public static GlobalConfig LoadGlobal(string path)
    {
        var cfg = new GlobalConfig
        {
            Agent = AgentNames.Auto,
            Agents = new List<string> { AgentNames.Auto },
            CiTimeout = Config.DefaultCiTimeout,
            LogLevel = "info",
        };

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return cfg;
        }
        catch (DirectoryNotFoundException)
        {
            return cfg;
        }

        var map = ParseRootMapping(text, "global config");
        if (map is null)
        {
            return cfg;
        }

        ValidateKnownFields(map, KnownGlobalFields, "global config", "globalConfigRaw");

        var agentNode = GetNode(map, "agent");
        if (agentNode is not null)
        {
            var agents = ParseAgentList(agentNode);
            if (agents.Count > 0)
            {
                cfg.Agents = agents;
                cfg.Agent = agents[0];
            }
        }

        var acpxPath = GetScalarString(map, "acpx_path");
        if (!string.IsNullOrEmpty(acpxPath))
        {
            cfg.AcpxPath = acpxPath;
        }

        var acpRegistry = GetStringMap(map, "acp_registry_overrides");
        if (acpRegistry is not null)
        {
            cfg.AcpRegistryOverrides = acpRegistry;
        }

        var pathOverride = GetStringMap(map, "agent_path_override");
        if (pathOverride is not null)
        {
            cfg.AgentPathOverride = pathOverride;
        }

        var argsOverride = GetStringListMap(map, "agent_args_override");
        if (argsOverride is not null)
        {
            ValidateAgentArgsOverride(argsOverride);
            cfg.AgentArgsOverride = argsOverride;
        }

        var timeoutValue = GetScalarString(map, "ci_timeout") ?? string.Empty;
        if (timeoutValue.Length == 0)
        {
            timeoutValue = GetScalarString(map, "babysit_timeout") ?? string.Empty;
        }
        if (timeoutValue.Length > 0)
        {
            cfg.CiTimeout = ParseCiTimeout(timeoutValue);
        }

        var logLevel = GetScalarString(map, "log_level");
        if (!string.IsNullOrEmpty(logLevel))
        {
            cfg.LogLevel = logLevel;
        }

        var autoFix = ParseAutoFixRaw(GetMapping(map, "auto_fix"));
        autoFix.Ci ??= autoFix.Babysit;
        cfg.AutoFix = autoFix;

        cfg.Intent = ParseIntentRaw(GetMapping(map, "intent"));
        cfg.Test = ParseTestRaw(GetMapping(map, "test"));

        return cfg;
    }

    /// <summary>
    /// Interprets the ci_timeout config value. The keyword "unlimited" (also
    /// "none"/"off"/"never"), or any non-positive duration, resolves to
    /// <see cref="Config.CiTimeoutUnlimited"/>; otherwise the value is parsed as a
    /// Go duration.
    /// </summary>
    public static TimeSpan ParseCiTimeout(string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "unlimited":
            case "none":
            case "off":
            case "never":
                return Config.CiTimeoutUnlimited;
        }

        TimeSpan d;
        try
        {
            d = GoDuration.Parse(value);
        }
        catch (FormatException ex)
        {
            throw new ConfigException($"parse ci_timeout \"{value}\": {ex.Message}", ex);
        }

        return d <= TimeSpan.Zero ? Config.CiTimeoutUnlimited : d;
    }

    /// <summary>Reads per-repo config from dir/.no-mistakes.yaml. Returns zero-value config if the file doesn't exist.</summary>
    public static RepoConfig LoadRepo(string dir)
    {
        var path = Path.Combine(dir, ".no-mistakes.yaml");
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new RepoConfig();
        }
        catch (DirectoryNotFoundException)
        {
            return new RepoConfig();
        }

        return ParseRepoConfig(text);
    }

    /// <summary>
    /// Parses per-repo config from raw YAML bytes. The trusted-config entry point:
    /// callers that read .no-mistakes.yaml from a specific git ref (e.g. the
    /// default branch) use this to avoid honoring a contributor's checked-out copy.
    /// </summary>
    public static RepoConfig LoadRepoFromBytes(byte[] data) => ParseRepoConfig(Encoding.UTF8.GetString(data));

    /// <summary>Parses per-repo config from a raw YAML string.</summary>
    public static RepoConfig LoadRepoFromString(string text) => ParseRepoConfig(text);

    private static RepoConfig ParseRepoConfig(string text)
    {
        var cfg = new RepoConfig();
        var map = ParseRootMapping(text, "repo config");
        if (map is null)
        {
            return cfg;
        }

        var agentNode = GetNode(map, "agent");
        if (agentNode is not null)
        {
            var agents = ParseAgentList(agentNode);
            cfg.Agent = agents.Count > 0 ? agents[0] : string.Empty;
            cfg.Agents = agents;
        }

        var commandsMap = GetMapping(map, "commands");
        if (commandsMap is not null)
        {
            cfg.Commands = new Commands
            {
                Lint = GetScalarString(commandsMap, "lint") ?? string.Empty,
                Test = GetScalarString(commandsMap, "test") ?? string.Empty,
                Format = GetScalarString(commandsMap, "format") ?? string.Empty,
            };
        }

        cfg.IgnorePatterns = GetStringList(map, "ignore_patterns") ?? new List<string>();
        cfg.AllowRepoCommands = GetBool(map, "allow_repo_commands") ?? false;

        var autoFix = ParseAutoFixRaw(GetMapping(map, "auto_fix"));
        autoFix.Ci ??= autoFix.Babysit;
        cfg.AutoFix = autoFix;

        cfg.Intent = ParseIntentRaw(GetMapping(map, "intent"));
        cfg.Test = ParseTestRaw(GetMapping(map, "test"));

        return cfg;
    }

    /// <summary>
    /// Returns the repo config that should drive the pipeline given a
    /// pushed-branch copy and the trusted default-branch copy. The code-executing
    /// selection fields (Commands and Agent/Agents) are taken only from the
    /// trusted copy unless the maintainer opted in via allowRepoCommands; when
    /// there is no trusted copy and no opt-in, they are forced empty so a
    /// contributor's pushed branch cannot inject shell or pick an agent.
    /// Non-executing fields always come from the pushed copy.
    /// </summary>
    public static RepoConfig EffectiveRepoConfig(RepoConfig? pushed, RepoConfig? trusted, bool allowRepoCommands)
    {
        pushed ??= new RepoConfig();
        var effective = ShallowClone(pushed);
        if (allowRepoCommands)
        {
            return effective;
        }

        if (trusted is not null)
        {
            effective.Commands = trusted.Commands;
            effective.Agent = trusted.Agent;
            effective.Agents = new List<string>(trusted.Agents);
        }
        else
        {
            effective.Commands = new Commands();
            effective.Agent = string.Empty;
            effective.Agents = new List<string>();
        }

        return effective;
    }

    /// <summary>Converts a log level string to a <see cref="LogLevel"/>. Unknown values default to Info.</summary>
    public static LogLevel ParseLogLevel(string level) => level switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>
    /// Combines global and per-repo config. Per-repo agent values, including
    /// ordered fallback lists, override global agent values when non-empty.
    /// Commands and ignore patterns come from repo config only.
    /// </summary>
    public static Config Merge(GlobalConfig global, RepoConfig repo)
    {
        var autoFix = AutoFixDefaults();
        ApplyAutoFixOverrides(ref autoFix, global.AutoFix);
        ApplyAutoFixOverrides(ref autoFix, repo.AutoFix);

        var intent = IntentDefaults();
        ApplyIntentOverrides(intent, global.Intent);
        ApplyIntentOverrides(intent, repo.Intent);

        var test = TestDefaults();
        ApplyTestOverrides(ref test, global.Test);
        ApplyTestOverrides(ref test, repo.Test);

        var cfg = new Config
        {
            Agent = global.Agent,
            Agents = new List<string>(global.Agents),
            AcpxPath = global.AcpxPath,
            AcpRegistryOverrides = global.AcpRegistryOverrides,
            AgentPathOverride = global.AgentPathOverride,
            AgentArgsOverride = global.AgentArgsOverride,
            CiTimeout = global.CiTimeout,
            LogLevel = global.LogLevel,
            Commands = repo.Commands,
            IgnorePatterns = repo.IgnorePatterns,
            AutoFix = autoFix,
            Intent = intent,
            Test = test,
        };

        if (!string.IsNullOrEmpty(repo.Agent))
        {
            cfg.Agent = repo.Agent;
            cfg.Agents = new List<string>(repo.Agents);
            if (cfg.Agents.Count == 0)
            {
                cfg.Agents = new List<string> { repo.Agent };
            }
        }

        return cfg;
    }

    /// <summary>Validates agent_args_override key names and rejects reserved flags.</summary>
    public static void ValidateAgentArgsOverride(Dictionary<string, List<string>> overrides)
    {
        foreach (var (name, args) in overrides)
        {
            if (!AgentArgsOverrideAgents.Contains(name))
            {
                throw new ConfigException($"invalid agent name in agent_args_override: \"{name}\" (valid: claude, codex, pi, copilot, droid)");
            }

            ReservedAgentArgs.TryGetValue(name, out var reserved);
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg.Trim().Length == 0)
                {
                    throw new ConfigException($"invalid agent_args_override.{name}[{i}]: empty arg");
                }

                var baseArg = arg;
                var idx = arg.IndexOf('=');
                if (idx > 0)
                {
                    baseArg = arg.Substring(0, idx);
                }
                if (reserved is not null && reserved.Contains(baseArg))
                {
                    throw new ConfigException($"invalid agent_args_override.{name}[{i}]: \"{arg}\" is managed by no-mistakes and cannot be overridden");
                }
            }
        }
    }

    private static RepoConfig ShallowClone(RepoConfig src) => new()
    {
        Agent = src.Agent,
        Agents = new List<string>(src.Agents),
        Commands = src.Commands,
        IgnorePatterns = src.IgnorePatterns,
        AllowRepoCommands = src.AllowRepoCommands,
        AutoFix = src.AutoFix,
        Intent = src.Intent,
        Test = src.Test,
    };

    private static AutoFix AutoFixDefaults() => new()
    {
        Lint = 3,
        Test = 3,
        Review = 0,
        Document = 3,
        Ci = 3,
        Rebase = 3,
    };

    private static void ApplyAutoFixOverrides(ref AutoFix dst, AutoFixRaw src)
    {
        if (src.Lint.HasValue)
        {
            dst.Lint = src.Lint.Value;
        }
        if (src.Test.HasValue)
        {
            dst.Test = src.Test.Value;
        }
        if (src.Review.HasValue)
        {
            dst.Review = src.Review.Value;
        }
        if (src.Document.HasValue)
        {
            dst.Document = src.Document.Value;
        }
        if (src.Ci.HasValue)
        {
            dst.Ci = src.Ci.Value;
        }
        if (src.Rebase.HasValue)
        {
            dst.Rebase = src.Rebase.Value;
        }
    }

    private static Intent IntentDefaults() => new()
    {
        Enabled = true,
        Threshold = 0.2,
        SlackDays = 3,
        DisabledReaders = new Dictionary<string, bool>(StringComparer.Ordinal),
    };

    private static void ApplyIntentOverrides(Intent dst, IntentRaw src)
    {
        if (src.Enabled.HasValue)
        {
            dst.Enabled = src.Enabled.Value;
        }
        if (src.Threshold.HasValue)
        {
            dst.Threshold = src.Threshold.Value;
        }
        if (src.SlackDays.HasValue)
        {
            dst.SlackDays = src.SlackDays.Value;
        }
        if (src.DisabledReaders.Count > 0)
        {
            foreach (var name in src.DisabledReaders)
            {
                dst.DisabledReaders[name.Trim().ToLowerInvariant()] = true;
            }
        }
    }

    private static Test TestDefaults() => new()
    {
        Evidence = new Evidence
        {
            StoreInRepo = false,
            Dir = ".no-mistakes/evidence",
        },
    };

    private static void ApplyTestOverrides(ref Test dst, TestRaw src)
    {
        if (src.Evidence.StoreInRepo.HasValue)
        {
            dst.Evidence.StoreInRepo = src.Evidence.StoreInRepo.Value;
        }
        if (src.Evidence.Dir is not null && src.Evidence.Dir.Trim().Length > 0)
        {
            dst.Evidence.Dir = src.Evidence.Dir.Trim();
        }
    }

    private static AutoFixRaw ParseAutoFixRaw(YamlMappingNode? map)
    {
        var raw = new AutoFixRaw();
        if (map is null)
        {
            return raw;
        }
        raw.Lint = GetInt(map, "lint");
        raw.Test = GetInt(map, "test");
        raw.Review = GetInt(map, "review");
        raw.Document = GetInt(map, "document");
        raw.Ci = GetInt(map, "ci");
        raw.Babysit = GetInt(map, "babysit");
        raw.Rebase = GetInt(map, "rebase");
        return raw;
    }

    private static IntentRaw ParseIntentRaw(YamlMappingNode? map)
    {
        var raw = new IntentRaw();
        if (map is null)
        {
            return raw;
        }
        raw.Enabled = GetBool(map, "enabled");
        raw.Threshold = GetDouble(map, "threshold");
        raw.SlackDays = GetInt(map, "slack_days");
        raw.DisabledReaders = GetStringList(map, "disabled_readers") ?? new List<string>();
        return raw;
    }

    private static TestRaw ParseTestRaw(YamlMappingNode? map)
    {
        var raw = new TestRaw();
        if (map is null)
        {
            return raw;
        }
        var evidence = GetMapping(map, "evidence");
        if (evidence is not null)
        {
            raw.Evidence.StoreInRepo = GetBool(evidence, "store_in_repo");
            raw.Evidence.Dir = GetScalarString(evidence, "dir");
        }
        return raw;
    }

    private static List<string> ParseAgentList(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                var name = (scalar.Value ?? string.Empty).Trim();
                return name.Length == 0 ? new List<string>() : new List<string> { name };
            case YamlSequenceNode sequence:
                var names = new List<string>();
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    if (sequence.Children[i] is not YamlScalarNode item)
                    {
                        throw new ConfigException($"agent[{i}] must be a string");
                    }
                    var value = (item.Value ?? string.Empty).Trim();
                    if (value.Length == 0)
                    {
                        throw new ConfigException($"agent[{i}] must not be empty");
                    }
                    names.Add(value);
                }
                return names;
            default:
                throw new ConfigException("agent must be a string or a list of strings");
        }
    }

    private static YamlMappingNode? ParseRootMapping(string text, string label)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"parse {label}: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        var root = stream.Documents[0].RootNode;
        if (root is null)
        {
            return null;
        }
        if (root is YamlMappingNode map)
        {
            return map;
        }
        // A blank document parses to an empty/null scalar; treat as no config.
        if (root is YamlScalarNode scalar && IsNullScalar(scalar))
        {
            return null;
        }
        throw new ConfigException($"parse {label}: expected a mapping at the document root");
    }

    private static void ValidateKnownFields(YamlMappingNode map, HashSet<string> known, string label, string typeName)
    {
        foreach (var entry in map.Children)
        {
            var key = ScalarKey(entry.Key);
            if (!known.Contains(key))
            {
                throw new ConfigException($"parse {label}: field {key} not found in type {typeName}");
            }
        }
    }

    private static YamlNode? GetNode(YamlMappingNode map, string key)
    {
        foreach (var entry in map.Children)
        {
            if (ScalarKey(entry.Key) == key)
            {
                return entry.Value;
            }
        }
        return null;
    }

    private static string ScalarKey(YamlNode key) => (key as YamlScalarNode)?.Value ?? string.Empty;

    private static bool IsNullScalar(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            return false;
        }
        var value = scalar.Value;
        return value is null || value.Length == 0 || value is "~" or "null" or "Null" or "NULL";
    }

    private static YamlMappingNode? GetMapping(YamlMappingNode map, string key) => GetNode(map, key) as YamlMappingNode;

    private static string? GetScalarString(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null)
        {
            return null;
        }
        if (node is YamlScalarNode scalar)
        {
            return IsNullScalar(scalar) ? null : scalar.Value;
        }
        throw new ConfigException($"{key} must be a scalar");
    }

    private static int? GetInt(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlScalarNode scalar && int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        throw new ConfigException($"{key} must be an integer");
    }

    private static bool? GetBool(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlScalarNode scalar)
        {
            var value = (scalar.Value ?? string.Empty).Trim();
            if (value is "true" or "True" or "TRUE")
            {
                return true;
            }
            if (value is "false" or "False" or "FALSE")
            {
                return false;
            }
        }
        throw new ConfigException($"{key} must be a boolean");
    }

    private static double? GetDouble(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlScalarNode scalar && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        throw new ConfigException($"{key} must be a number");
    }

    private static List<string>? GetStringList(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlSequenceNode sequence)
        {
            var list = new List<string>();
            foreach (var item in sequence.Children)
            {
                if (item is not YamlScalarNode scalar)
                {
                    throw new ConfigException($"{key} must be a list of strings");
                }
                list.Add(scalar.Value ?? string.Empty);
            }
            return list;
        }
        throw new ConfigException($"{key} must be a list");
    }

    private static Dictionary<string, string>? GetStringMap(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlMappingNode mapping)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in mapping.Children)
            {
                dict[ScalarKey(entry.Key)] = (entry.Value as YamlScalarNode)?.Value ?? string.Empty;
            }
            return dict;
        }
        throw new ConfigException($"{key} must be a mapping");
    }

    private static Dictionary<string, List<string>>? GetStringListMap(YamlMappingNode map, string key)
    {
        var node = GetNode(map, key);
        if (node is null || IsNullScalar(node))
        {
            return null;
        }
        if (node is YamlMappingNode mapping)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in mapping.Children)
            {
                var list = new List<string>();
                if (entry.Value is YamlSequenceNode sequence)
                {
                    foreach (var item in sequence.Children)
                    {
                        list.Add((item as YamlScalarNode)?.Value ?? string.Empty);
                    }
                }
                else if (entry.Value is YamlScalarNode scalar)
                {
                    list.Add(scalar.Value ?? string.Empty);
                }
                dict[ScalarKey(entry.Key)] = list;
            }
            return dict;
        }
        throw new ConfigException($"{key} must be a mapping");
    }
}
