using NoMistakes.Scm;
using Xunit;
using GitHubHost = NoMistakes.Scm.GitHub.Host;
using GitHubRemoteUrl = NoMistakes.Scm.GitHub.RemoteUrl;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm/github tests for the gh command wrapper: argument
/// construction, host-scoped auth, and check/state normalization. PR lookup
/// (FindPR) and fork routing land in slice 6c.
/// </summary>
public class GitHubHostTests
{
    private static GitHubHost NewHost(
        IReadOnlyDictionary<string, FakeCommandResponse> responses,
        Func<bool>? cliAvailable = null, string host = "", string repo = "")
        => new(ScmCommandFakes.Runner(responses), cliAvailable, host, repo);

    [Theory]
    // github.com inputs keep the plain owner/name format.
    [InlineData("github.com https", "https://github.com/test/repo", "test/repo")]
    [InlineData("github.com https with .git suffix", "https://github.com/test/repo.git", "test/repo")]
    [InlineData("github.com pr url", "https://github.com/test/repo/pull/42", "test/repo")]
    [InlineData("github.com ssh scp form", "git@github.com:test/repo.git", "test/repo")]
    [InlineData("github.com ssh url form", "ssh://git@github.com/test/repo.git", "test/repo")]
    [InlineData("github.com https with port", "https://github.com:8443/test/repo", "test/repo")]
    [InlineData("github.com mixed case host", "https://GitHub.com/test/repo.git", "test/repo")]
    [InlineData("github.com trailing slash", "https://github.com/test/repo/", "test/repo")]
    // GitHub Enterprise Server inputs get the host prefix gh requires.
    [InlineData("ghe https", "https://bbgithub.dev.bloomberg.com/org/repo", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe https with .git suffix", "https://bbgithub.dev.bloomberg.com/org/repo.git", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe ssh scp form", "git@bbgithub.dev.bloomberg.com:org/repo.git", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe ssh url form", "ssh://git@bbgithub.dev.bloomberg.com/org/repo.git", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe pr url", "https://bbgithub.dev.bloomberg.com/org/repo/pull/42", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe https with port", "https://bbgithub.dev.bloomberg.com:8443/org/repo.git", "bbgithub.dev.bloomberg.com/org/repo")]
    [InlineData("ghe trailing slash", "https://bbgithub.dev.bloomberg.com/org/repo/", "bbgithub.dev.bloomberg.com/org/repo")]
    // Empty/malformed inputs return "" so the --repo flag is omitted.
    [InlineData("empty", "", "")]
    [InlineData("host only ghe", "https://bbgithub.dev.bloomberg.com/", "")]
    [InlineData("owner only ghe", "https://bbgithub.dev.bloomberg.com/onlyowner", "")]
    public void HostPrefixedSlug(string name, string input, string want)
    {
        Assert.True(
            GitHubRemoteUrl.HostPrefixedSlug(input) == want,
            $"{name}: HostPrefixedSlug({input}) = {GitHubRemoteUrl.HostPrefixedSlug(input)}, want {want}");
    }

    [Fact]
    public async Task AvailableScopesAuthToConfiguredHost()
    {
        // With a known host, the auth check must be scoped via --hostname so
        // a stale credential on some other configured gh host (e.g.
        // github.com vs a GHE instance) cannot make this repo look
        // unauthenticated. The unscoped form is treated as a failure here to
        // prove the scoped form is the one actually invoked.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh auth status --hostname ghe.example.com"] = new(),
            ["gh auth status"] = new(Stderr: "github.com: token invalid\n", Code: 1),
        }, cliAvailable: () => true, host: "ghe.example.com");

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableFallsBackToUnscopedAuthWhenHostUnknown()
    {
        // No host -> behave as before: a bare `gh auth status`.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh auth status"] = new(),
        }, cliAvailable: () => true);

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsMissingCli()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>(), cliAvailable: () => false);

        Assert.Equal("gh CLI is not installed", await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsUnauthenticatedWhenScopedCheckFails()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh auth status --hostname ghe.example.com"] = new(Stderr: "not logged in\n", Code: 1),
        }, cliAvailable: () => true, host: "ghe.example.com");

        Assert.Equal("gh CLI is not authenticated", await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task GetChecksPassesRepoFlag()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr checks 123 --repo test/repo --json name,state,bucket,completedAt"] = new(
                Stdout: """[{"name":"build","state":"SUCCESS","bucket":"pass"}]""" + "\n"),
        }, repo: "test/repo");

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        var check = Assert.Single(checks);
        Assert.Equal("build", check.Name);
        Assert.Equal(CheckBucket.Pass, check.Bucket);
    }

    [Fact]
    public async Task GetPRStatePassesRepoFlag()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr view 123 --repo test/repo --json state --jq .state"] = new(Stdout: "MERGED\n"),
        }, repo: "test/repo");

        var state = await host.GetPRStateAsync(new PullRequest { Number = "123" });

        Assert.Equal(PullRequestState.Merged, state);
    }

    [Fact]
    public async Task CreatePRStreamsBodyThroughStdin()
    {
        const string body = "## What Changed\n\n- keep generated pull request bodies postable";
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr create --head feature/body-cap --base main --repo test/repo --title fix: cap body --body-file -"] = new(
                Stdout: "https://github.com/test/repo/pull/42\n", WantStdin: body),
        }, repo: "test/repo");

        var pr = await host.CreatePRAsync(
            "feature/body-cap", "main", new PullRequestContent("fix: cap body", body));

        Assert.Equal("42", pr.Number);
        Assert.Equal("https://github.com/test/repo/pull/42", pr.Url);
    }

    [Fact]
    public async Task UpdatePRStreamsBodyThroughStdin()
    {
        const string body = "## What Changed\n\n- update existing pull request bodies without long argv";
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr edit 42 --repo test/repo --title fix: cap body --body-file -"] = new(WantStdin: body),
        }, repo: "test/repo");

        var pr = new PullRequest { Number = "42", Url = "https://github.com/test/repo/pull/42" };
        var updated = await host.UpdatePRAsync(pr, new PullRequestContent("fix: cap body", body));

        Assert.Same(pr, updated);
    }

    [Fact]
    public async Task GetChecksFallsBackToStateWhenBucketMissing()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr checks 123 --json name,state,bucket,completedAt"] = new(
                Stdout: """[{"name":"build","state":"FAILURE","bucket":""},{"name":"tests","state":"PENDING","bucket":""}]""" + "\n"),
        });

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        Assert.Equal(2, checks.Count);
        Assert.Equal("build", checks[0].Name);
        Assert.Equal(CheckBucket.Fail, checks[0].Bucket);
        Assert.Equal("tests", checks[1].Name);
        Assert.Equal(CheckBucket.Pending, checks[1].Bucket);
    }

    [Fact]
    public async Task GetChecksParsesCompletedAt()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr checks 123 --json name,state,bucket,completedAt"] = new(
                Stdout: """[{"name":"build","state":"FAILURE","bucket":"fail","completedAt":"2026-04-24T04:15:00Z"},{"name":"tests","state":"SUCCESS","bucket":"pass","completedAt":"not-a-time"}]""" + "\n"),
        });

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        Assert.Equal(2, checks.Count);
        Assert.Equal(new DateTimeOffset(2026, 4, 24, 4, 15, 0, TimeSpan.Zero), checks[0].CompletedAt);
        Assert.Null(checks[1].CompletedAt);
    }

    [Fact]
    public async Task GetChecksTreatsNoChecksReportedAsEmpty()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr checks 123 --json name,state,bucket,completedAt"] = new(
                Stderr: "no checks reported on the 'feature' branch\n", Code: 1),
        });

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        Assert.Empty(checks);
    }

    [Fact]
    public async Task GetMergeableStateNormalizesConflicting()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh pr view 123 --json mergeable --jq .mergeable"] = new(Stdout: "CONFLICTING\n"),
        });

        var state = await host.GetMergeableStateAsync(new PullRequest { Number = "123" });

        Assert.Equal(MergeableState.Conflicting, state);
        Assert.True(state.IsConflict());
        Assert.True(state.IsResolved());
    }

    [Fact]
    public async Task FetchFailedCheckLogsSelectsMatchingRunForHeadSHA()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["gh run list --branch feature --commit abc123 --status failure --limit 20 --json databaseId,headSha,name,displayTitle,workflowName"] = new(
                Stdout: """[{"databaseId":101,"headSha":"abc123","name":"CI","displayTitle":"feature","workflowName":"CI"},{"databaseId":102,"headSha":"abc123","name":"Lint","displayTitle":"lint","workflowName":"Lint"}]""" + "\n"),
            ["gh run view 101 --json jobs"] = new(
                Stdout: """{"jobs":[{"name":"unit","conclusion":"failure"}]}""" + "\n"),
            ["gh run view 102 --json jobs"] = new(
                Stdout: """{"jobs":[{"name":"lint","conclusion":"failure"}]}""" + "\n"),
            ["gh run view 102 --log-failed"] = new(Stdout: "lint failed\n"),
        });

        var logs = await host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "123" }, "feature", "abc123", new[] { "lint" });

        Assert.Equal("lint failed", logs);
    }
}
