using NoMistakes.Scm;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the fork-routing guards from Go's pipeline buildHost
/// (internal/pipeline/steps/host.go): GitHub is the only provider with
/// end-to-end fork PR routing; a configured fork_url on any other provider
/// must fail closed with a skip reason instead of opening a self PR.
/// </summary>
public class ForkRoutingTests
{
    private const string ForkUrl = "https://example.com/contributor/repo.git";

    [Fact]
    public void NoForkUrlNeverSkips()
    {
        foreach (var provider in new[]
        {
            Provider.GitHub, Provider.GitLab, Provider.Bitbucket, Provider.AzureDevOps,
        })
        {
            Assert.Null(ForkRouting.SkipReason(provider, ""));
            Assert.Null(ForkRouting.SkipReason(provider, "   "));
        }
    }

    [Fact]
    public void GitHubForkRoutingIsSupported()
    {
        Assert.Null(ForkRouting.SkipReason(Provider.GitHub, ForkUrl));
    }

    [Theory]
    [InlineData(Provider.GitLab, "fork PR routing for GitLab is not implemented")]
    [InlineData(Provider.Bitbucket, "fork PR routing for Bitbucket is not implemented")]
    [InlineData(Provider.AzureDevOps, "fork PR routing for Azure DevOps is not implemented")]
    public void NonGitHubForkRoutingFailsClosed(Provider provider, string wantReason)
    {
        Assert.Equal(wantReason, ForkRouting.SkipReason(provider, ForkUrl));
    }
}
