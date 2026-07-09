using YamlDotNet.Serialization;

namespace NoMistakes.Scm;

/// <summary>
/// Detects which SCM provider a git remote URL belongs to. Mirrors Go's
/// <c>internal/scm.DetectProvider</c>: well-known hosts are matched by
/// substring; anything else falls back to the glab and gh CLI config files so
/// self-hosted GitLab and GitHub Enterprise instances the user has configured
/// are recognized without hardcoding any host.
/// </summary>
public static class ProviderDetector
{
    public static Provider Detect(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("github.com"))
        {
            return Provider.GitHub;
        }
        if (lower.Contains("gitlab.com") || lower.Contains("gitlab."))
        {
            return Provider.GitLab;
        }
        if (lower.Contains("bitbucket.org"))
        {
            return Provider.Bitbucket;
        }
        if (lower.Contains("dev.azure.com") || lower.Contains("visualstudio.com"))
        {
            // Covers dev.azure.com, ssh.dev.azure.com, {org}.visualstudio.com,
            // and the legacy vs-ssh.visualstudio.com SSH host.
            return Provider.AzureDevOps;
        }

        // Fallback for self-hosted GitLab instances whose hostname carries no
        // "gitlab" marker: consult the glab CLI's configured hosts. If the
        // remote's host (or a host's api_host) is one glab is configured to
        // talk to, treat it as GitLab. This reads whatever the user configured
        // at runtime; no host is hardcoded.
        //
        // Fallback for GitHub Enterprise Server instances: consult the gh
        // CLI's configured hosts (hosts.yml). If the remote's host is one gh
        // is authenticated with, treat it as GitHub.
        var host = RemoteHost.ExtractHost(url);
        if (host.Length > 0)
        {
            if (GlabKnowsHost(host))
            {
                return Provider.GitLab;
            }
            if (GhKnowsHost(host))
            {
                return Provider.GitHub;
            }
        }

        return Provider.Unknown;
    }

    private sealed class GlabConfig
    {
        [YamlMember(Alias = "hosts")]
        public Dictionary<string, GlabHost?>? Hosts { get; set; }
    }

    private sealed class GlabHost
    {
        [YamlMember(Alias = "api_host")]
        public string? ApiHost { get; set; }
    }

    /// <summary>
    /// Reports whether host appears in glab's configured hosts map, either as
    /// a top-level key or as a host's api_host. Any read/parse error is
    /// treated as "not configured" so detection fails closed to Unknown.
    /// </summary>
    private static bool GlabKnowsHost(string host)
    {
        var path = GlabConfigPath();
        if (path is null)
        {
            return false;
        }
        GlabConfig? cfg;
        try
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            cfg = deserializer.Deserialize<GlabConfig?>(File.ReadAllText(path));
        }
        catch
        {
            return false;
        }
        if (cfg?.Hosts is null)
        {
            return false;
        }
        host = host.ToLowerInvariant();
        foreach (var (key, entry) in cfg.Hosts)
        {
            if (key.Trim().ToLowerInvariant() == host)
            {
                return true;
            }
            var api = entry?.ApiHost?.Trim().ToLowerInvariant() ?? "";
            if (api.Length > 0 && RemoteHost.ExtractHost(api) == host)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves glab's config file location, preferring $GLAB_CONFIG_DIR, then
    /// $XDG_CONFIG_HOME/glab-cli, then ~/.config/glab-cli. Returns null when
    /// no home/config directory can be determined.
    /// </summary>
    private static string? GlabConfigPath()
    {
        var dir = Environment.GetEnvironmentVariable("GLAB_CONFIG_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            return Path.Combine(dir, "config.yml");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "glab-cli", "config.yml");
        }
        var home = HomeDir();
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }
        return Path.Combine(home, ".config", "glab-cli", "config.yml");
    }

    /// <summary>
    /// Reports whether host appears as a top-level key in gh's hosts.yml. Any
    /// read/parse error is treated as "not configured" so detection fails
    /// closed to Unknown.
    /// </summary>
    private static bool GhKnowsHost(string host)
    {
        var path = GhConfigPath();
        if (path is null)
        {
            return false;
        }
        Dictionary<string, object?>? hosts;
        try
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            hosts = deserializer.Deserialize<Dictionary<string, object?>?>(File.ReadAllText(path));
        }
        catch
        {
            return false;
        }
        if (hosts is null)
        {
            return false;
        }
        host = host.ToLowerInvariant();
        foreach (var key in hosts.Keys)
        {
            if (key.Trim().ToLowerInvariant() == host)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves gh's hosts config file location, preferring $GH_CONFIG_DIR,
    /// then $XDG_CONFIG_HOME/gh, then ~/.config/gh. Returns null when no
    /// home/config directory can be determined.
    /// </summary>
    private static string? GhConfigPath()
    {
        var dir = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            return Path.Combine(dir, "hosts.yml");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "gh", "hosts.yml");
        }
        var home = HomeDir();
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }
        return Path.Combine(home, ".config", "gh", "hosts.yml");
    }

    private static string HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }
        return home;
    }
}
