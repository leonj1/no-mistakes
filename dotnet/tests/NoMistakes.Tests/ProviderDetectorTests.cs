using NoMistakes.Scm;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm TestDetectProvider* suite. Detection falls back to
/// the glab/gh CLI config files for self-hosted GitLab and GitHub Enterprise
/// hosts, so every test pins GLAB_CONFIG_DIR and GH_CONFIG_DIR (to an empty or
/// synthetic dir) to keep a real CLI install on the host from influencing the
/// assertions. Environment variables are process-global; the tests in this
/// class run serially (one xunit collection per class), and each one restores
/// the prior values on exit.
/// </summary>
public class ProviderDetectorTests
{
    private static void WithConfigDirs(string glabDir, string ghDir, Action body)
    {
        var oldGlab = Environment.GetEnvironmentVariable("GLAB_CONFIG_DIR");
        var oldGh = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GLAB_CONFIG_DIR", glabDir);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", ghDir);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLAB_CONFIG_DIR", oldGlab);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", oldGh);
        }
    }

    [Fact]
    public void Detect_KnownProviderHosts()
    {
        var cases = new (string Url, Provider Want)[]
        {
            // GitHub: HTTPS and SSH.
            ("https://github.com/user/repo.git", Provider.GitHub),
            ("git@github.com:user/repo.git", Provider.GitHub),
            ("ssh://git@github.com/user/repo.git", Provider.GitHub),
            // GitLab: HTTPS, SSH, subgroups, and gitlab-named self-hosts.
            ("https://gitlab.com/user/repo.git", Provider.GitLab),
            ("git@gitlab.com:user/repo.git", Provider.GitLab),
            ("https://gitlab.com/group/subgroup/repo.git", Provider.GitLab),
            ("git@gitlab.com:group/subgroup/deeper/repo.git", Provider.GitLab),
            ("https://gitlab.mycorp.com/group/repo.git", Provider.GitLab),
            // Bitbucket: HTTPS and SSH.
            ("https://bitbucket.org/user/repo.git", Provider.Bitbucket),
            ("git@bitbucket.org:user/repo.git", Provider.Bitbucket),
            // Azure DevOps: modern and legacy hosts, HTTPS and SSH.
            ("https://dev.azure.com/org/project/_git/repo", Provider.AzureDevOps),
            ("git@ssh.dev.azure.com:v3/org/project/repo", Provider.AzureDevOps),
            ("https://org.visualstudio.com/project/_git/repo", Provider.AzureDevOps),
            ("git@vs-ssh.visualstudio.com:v3/org/project/repo", Provider.AzureDevOps),
            // Unknown and malformed inputs.
            ("https://example.com/user/repo.git", Provider.Unknown),
            ("", Provider.Unknown),
            ("   ", Provider.Unknown),
            ("not a remote url", Provider.Unknown),
        };

        using var glab = new TempDir();
        using var gh = new TempDir();
        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            foreach (var (url, want) in cases)
            {
                Assert.Equal(want, ProviderDetector.Detect(url));
            }
        });
    }

    private static TempDir WriteGlabConfig(string body)
    {
        var dir = new TempDir();
        File.WriteAllText(dir.File("config.yml"), body);
        return dir;
    }

    private static TempDir WriteGhConfig(string body)
    {
        var dir = new TempDir();
        File.WriteAllText(dir.File("hosts.yml"), body);
        return dir;
    }

    [Fact]
    public void Detect_SelfHostedGitLabViaGlabConfig()
    {
        using var glab = WriteGlabConfig(
            "hosts:\n" +
            "    gitlab.example.com:\n" +
            "        token: xxx\n" +
            "        api_host: gitlab.example.com\n" +
            "        api_protocol: https\n");
        using var gh = new TempDir();

        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            var cases = new[]
            {
                "https://gitlab.example.com/group/repo.git",
                "git@gitlab.example.com:group/repo.git",
                "ssh://git@gitlab.example.com:22/group/repo.git",
                "https://gitlab.example.com/group/subgroup/repo.git",
            };
            foreach (var url in cases)
            {
                Assert.Equal(Provider.GitLab, ProviderDetector.Detect(url));
            }

            // A host not in the config still resolves to unknown.
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://other.example.org/group/repo.git"));
        });
    }

    [Fact]
    public void Detect_SelfHostedGitLabViaApiHost()
    {
        // The remote host differs from the config key but matches api_host.
        using var glab = WriteGlabConfig(
            "hosts:\n" +
            "    git.example.com:\n" +
            "        token: xxx\n" +
            "        api_host: api.example.com\n");
        using var gh = new TempDir();

        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            Assert.Equal(Provider.GitLab, ProviderDetector.Detect("https://api.example.com/group/repo.git"));
            Assert.Equal(Provider.GitLab, ProviderDetector.Detect("https://git.example.com/group/repo.git"));
        });
    }

    [Fact]
    public void Detect_GlabConfigMissingFailsClosed()
    {
        using var glab = new TempDir();
        using var gh = new TempDir();
        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://selfhosted.example.com/group/repo.git"));
        });
    }

    [Fact]
    public void Detect_GlabConfigMalformedFailsClosed()
    {
        using var glab = WriteGlabConfig("this: is: not: valid: yaml: ::::\n\t- broken");
        using var gh = new TempDir();
        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://selfhosted.example.com/group/repo.git"));
        });
    }

    [Fact]
    public void Detect_GitHubEnterpriseViaGhConfig()
    {
        using var glab = new TempDir();
        using var gh = WriteGhConfig(
            "ghe.corp.example.com:\n" +
            "    user: someuser\n" +
            "    oauth_token: xxx\n" +
            "    git_protocol: ssh\n");

        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            var cases = new[]
            {
                "git@ghe.corp.example.com:org/repo.git",
                "https://ghe.corp.example.com/org/repo.git",
                "ssh://git@ghe.corp.example.com/org/repo.git",
            };
            foreach (var url in cases)
            {
                Assert.Equal(Provider.GitHub, ProviderDetector.Detect(url));
            }

            // A host not in the config still resolves to unknown.
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://other.example.org/org/repo.git"));
        });
    }

    [Fact]
    public void Detect_GhConfigMissingFailsClosed()
    {
        using var glab = new TempDir();
        using var gh = new TempDir();
        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://ghe.example.com/org/repo.git"));
        });
    }

    [Fact]
    public void Detect_GhConfigMalformedFailsClosed()
    {
        using var glab = new TempDir();
        using var gh = WriteGhConfig("this: is: not: valid: yaml: ::::\n\t- broken");
        WithConfigDirs(glab.Path, gh.Path, () =>
        {
            Assert.Equal(Provider.Unknown, ProviderDetector.Detect("https://ghe.example.com/org/repo.git"));
        });
    }
}
