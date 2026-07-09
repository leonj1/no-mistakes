using NoMistakes.Scm;
using Xunit;
using GitHubRemoteUrl = NoMistakes.Scm.GitHub.RemoteUrl;
using GitLabRemoteUrl = NoMistakes.Scm.GitLab.RemoteUrl;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm TestExtractHost, internal/scm/github TestRepoSlug,
/// and internal/scm/gitlab TestProjectPath.
/// </summary>
public class ScmUrlHelperTests
{
    [Theory]
    [InlineData("https with .git", "https://github.com/user/repo.git", "github.com")]
    [InlineData("scp ssh", "git@github.com:user/repo.git", "github.com")]
    [InlineData("https self-hosted", "https://gitlab.example.com/group/repo.git", "gitlab.example.com")]
    [InlineData("scp ssh nested path", "git@gitlab.example.com:group/sub/repo.git", "gitlab.example.com")]
    [InlineData("ssh url with port", "ssh://git@code.example.com:2222/group/repo.git", "code.example.com")]
    [InlineData("https userinfo and port", "https://user:token@code.example.com:8443/group/repo.git", "code.example.com")]
    [InlineData("git protocol", "git://code.example.com/group/repo.git", "code.example.com")]
    [InlineData("mixed case lowercased", "https://CODE.Example.COM/group/repo", "code.example.com")]
    [InlineData("ipv6 literal with port", "ssh://git@[::1]:22/group/repo.git", "[::1]")]
    // A '@' inside the path must not be mistaken for a "user@" userinfo
    // prefix: host extraction has to split off the path first.
    [InlineData("at-sign in path https", "https://code.example.com/group@prod/repo.git", "code.example.com")]
    [InlineData("at-sign in path scp", "git@code.example.com:group@prod/repo.git", "code.example.com")]
    [InlineData("at-sign in path with userinfo", "https://user:token@code.example.com/group@prod/repo.git", "code.example.com")]
    [InlineData("empty", "", "")]
    public void ExtractHost(string name, string remote, string want)
    {
        Assert.True(RemoteHost.ExtractHost(remote) == want,
            $"{name}: ExtractHost({remote}) = {RemoteHost.ExtractHost(remote)}, want {want}");
    }

    [Theory]
    [InlineData("https", "https://github.com/test/repo", "test/repo")]
    [InlineData("https with .git suffix", "https://github.com/test/repo.git", "test/repo")]
    [InlineData("pr url", "https://github.com/test/repo/pull/42", "test/repo")]
    [InlineData("ssh scp form", "git@github.com:test/repo.git", "test/repo")]
    [InlineData("ssh scp form no suffix", "git@github.com:test/repo", "test/repo")]
    [InlineData("ssh url form", "ssh://git@github.com/test/repo.git", "test/repo")]
    [InlineData("https with port", "https://github.com:8443/test/repo", "test/repo")]
    [InlineData("already a slug", "test/repo", "test/repo")]
    [InlineData("trailing slash", "https://github.com/test/repo/", "test/repo")]
    [InlineData("empty", "", "")]
    [InlineData("host only", "https://github.com/", "")]
    [InlineData("owner only", "https://github.com/onlyowner", "")]
    public void GitHubRepoSlug(string name, string input, string want)
    {
        var got = GitHubRemoteUrl.RepoSlug(input);
        Assert.True(got == want, $"{name}: RepoSlug({input}) = {got}, want {want}");
    }

    [Theory]
    [InlineData("https with .git", "https://gitlab.example.com/group/project.git", "group/project")]
    [InlineData("https without .git", "https://gitlab.example.com/group/project", "group/project")]
    [InlineData("https nested subgroups", "https://gitlab.example.com/group/sub/project.git", "group/sub/project")]
    [InlineData("https trailing slash", "https://gitlab.example.com/group/project/", "group/project")]
    [InlineData("scp ssh", "git@gitlab.example.com:group/project.git", "group/project")]
    [InlineData("scp ssh nested", "git@gitlab.example.com:group/sub/project.git", "group/sub/project")]
    // scp-style without a "user@" prefix must still yield the project path;
    // an empty path here would drop the REST job read back to branch-dependent
    // `glab ci get`, which fails in the daemon's detached-HEAD worktree.
    [InlineData("scp ssh no user", "gitlab.example.com:group/project.git", "group/project")]
    [InlineData("scp ssh no user nested", "gitlab.example.com:group/sub/project.git", "group/sub/project")]
    [InlineData("ssh url", "ssh://git@gitlab.example.com:22/group/project.git", "group/project")]
    [InlineData("empty", "", "")]
    [InlineData("host only", "https://gitlab.example.com", "")]
    // A Windows local filesystem path carries a drive-letter colon, but it is
    // not scp-style host:path syntax: it must not be parsed into a project
    // path or the job read would target a non-existent REST project.
    [InlineData("windows drive path backslash", @"C:\Users\me\repo", "")]
    [InlineData("windows drive path forward slash", "C:/Users/me/repo", "")]
    public void GitLabProjectPath(string name, string input, string want)
    {
        var got = GitLabRemoteUrl.ProjectPath(input);
        Assert.True(got == want, $"{name}: ProjectPath({input}) = {got}, want {want}");
    }
}
