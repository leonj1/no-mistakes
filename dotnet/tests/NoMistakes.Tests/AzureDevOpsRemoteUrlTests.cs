using NoMistakes.Scm.AzureDevOps;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm/azuredevops TestParseRemote and TestWebPRURL.
/// </summary>
public class AzureDevOpsRemoteUrlTests
{
    [Fact]
    public void TryParseRemote_SupportedForms()
    {
        var cases = new (string Name, string In, string WantOrgUrl, string WantProject, string WantRepo, bool WantOk)[]
        {
            ("https dev.azure.com",
                "https://dev.azure.com/myorg/myproject/_git/myrepo",
                "https://dev.azure.com/myorg", "myproject", "myrepo", true),
            ("https with .git suffix",
                "https://dev.azure.com/myorg/myproject/_git/myrepo.git",
                "https://dev.azure.com/myorg", "myproject", "myrepo", true),
            ("https with org userinfo prefix",
                "https://myorg@dev.azure.com/myorg/myproject/_git/myrepo",
                "https://dev.azure.com/myorg", "myproject", "myrepo", true),
            ("ssh scp form",
                "git@ssh.dev.azure.com:v3/myorg/myproject/myrepo",
                "https://dev.azure.com/myorg", "myproject", "myrepo", true),
            ("pr url suffix is ignored",
                "https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/42",
                "https://dev.azure.com/myorg", "myproject", "myrepo", true),
            ("project name with spaces is decoded",
                "https://dev.azure.com/myorg/My%20Project/_git/myrepo",
                "https://dev.azure.com/myorg", "My Project", "myrepo", true),
            ("legacy visualstudio.com",
                "https://myorg.visualstudio.com/myproject/_git/myrepo",
                "https://myorg.visualstudio.com", "myproject", "myrepo", true),
            ("legacy visualstudio.com with DefaultCollection",
                "https://myorg.visualstudio.com/DefaultCollection/myproject/_git/myrepo",
                "https://myorg.visualstudio.com", "myproject", "myrepo", true),
            ("legacy vs-ssh visualstudio.com",
                "git@vs-ssh.visualstudio.com:v3/myorg/myproject/myrepo",
                "https://myorg.visualstudio.com", "myproject", "myrepo", true),
            ("empty", "", "", "", "", false),
            ("whitespace only", "   ", "", "", "", false),
            ("github https is rejected", "https://github.com/owner/repo", "", "", "", false),
            ("github with extra path is rejected", "https://github.com/owner/repo/tree/main", "", "", "", false),
            ("github ssh is rejected", "git@github.com:owner/repo.git", "", "", "", false),
            ("azure missing repo", "https://dev.azure.com/myorg/myproject/_git", "", "", "", false),
            ("azure ssh too few segments", "git@ssh.dev.azure.com:v3/myorg/myproject", "", "", "", false),
            ("no scheme and no colon", "dev.azure.com/myorg/myproject/_git/myrepo", "", "", "", false),
        };

        foreach (var tc in cases)
        {
            var ok = RemoteUrl.TryParseRemote(tc.In, out var remote);
            Assert.True(ok == tc.WantOk, $"{tc.Name}: TryParseRemote({tc.In}) ok = {ok}, want {tc.WantOk}");
            if (!tc.WantOk)
            {
                continue;
            }
            Assert.True(
                remote.OrgUrl == tc.WantOrgUrl && remote.Project == tc.WantProject && remote.Repo == tc.WantRepo,
                $"{tc.Name}: TryParseRemote({tc.In}) = ({remote.OrgUrl}, {remote.Project}, {remote.Repo}), " +
                $"want ({tc.WantOrgUrl}, {tc.WantProject}, {tc.WantRepo})");
        }
    }

    [Fact]
    public void WebPRUrl_ConstructedFromOrgProjectRepo()
    {
        var got = RemoteUrl.WebPRUrl("https://dev.azure.com/myorg", "myproject", "myrepo", repoWebUrl: "", id: "42");
        Assert.Equal("https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/42", got);
    }

    [Fact]
    public void WebPRUrl_PrefersRepositoryWebUrl()
    {
        var got = RemoteUrl.WebPRUrl(
            "https://dev.azure.com/myorg", "ignored", "ignored",
            repoWebUrl: "https://dev.azure.com/myorg/myproject/_git/myrepo", id: "7");
        Assert.Equal("https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/7", got);
    }

    [Fact]
    public void WebPRUrl_EncodesSpaces()
    {
        var got = RemoteUrl.WebPRUrl("https://dev.azure.com/myorg", "My Project", "my repo", repoWebUrl: "", id: "1");
        Assert.Equal("https://dev.azure.com/myorg/My%20Project/_git/my%20repo/pullrequest/1", got);
    }
}
