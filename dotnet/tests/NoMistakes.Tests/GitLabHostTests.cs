using NoMistakes.Scm;
using Xunit;
using GitLabHost = NoMistakes.Scm.GitLab.Host;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm/gitlab tests for the glab command wrapper:
/// argument construction (incl. the removed --state flag and the
/// branch-independent `glab api` job reads), host-scoped auth, and job
/// parsing. MR lookup (FindPR) and fork routing land in slice 6c.
/// </summary>
public class GitLabHostTests
{
    private static GitLabHost NewHost(
        IReadOnlyDictionary<string, FakeCommandResponse> responses,
        Func<bool>? cliAvailable = null, string host = "", string projectPath = "")
        => new(ScmCommandFakes.Runner(responses), cliAvailable, host, projectPath);

    [Fact]
    public async Task AvailableScopesAuthToConfiguredHost()
    {
        // With a known host, the auth check must be scoped via --hostname so
        // a stale credential on some other configured glab instance cannot
        // make this repo look unauthenticated. The unscoped form is treated
        // as a failure here to prove the scoped form is the one actually
        // invoked.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab auth status --hostname gitlab.example.com"] = new(),
            ["glab auth status"] = new(Stderr: "gitlab.com: token invalid\n", Code: 1),
        }, cliAvailable: () => true, host: "gitlab.example.com");

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableFallsBackToUnscopedAuthWhenHostUnknown()
    {
        // No host -> behave as before: a bare `glab auth status`.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab auth status"] = new(),
        }, cliAvailable: () => true);

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsMissingCli()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>(), cliAvailable: () => false);

        Assert.Equal("glab CLI is not installed", await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsUnauthenticatedWhenScopedCheckFails()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab auth status --hostname gitlab.example.com"] = new(Stderr: "no token\n", Code: 1),
        }, cliAvailable: () => true, host: "gitlab.example.com");

        Assert.Equal("glab CLI is not authenticated", await host.CheckAvailabilityAsync());
    }

    [Theory]
    [InlineData("draft", "draft_status")]
    [InlineData("discussions unresolved", "discussions_not_resolved")]
    [InlineData("blocked", "blocked_status")]
    public async Task GetMergeableStateTreatsBlockedStatusesAsResolved(string name, string status)
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr view 123 --output json"] = new(
                Stdout: $$"""{"iid":123,"state":"opened","detailed_merge_status":"{{status}}"}""" + "\n"),
        });

        var got = await host.GetMergeableStateAsync(new PullRequest { Number = "123" });

        Assert.True(
            got == MergeableState.Mergeable,
            $"{name}: GetMergeableState({status}) = {got}, want Mergeable");
    }

    [Fact]
    public async Task CreatePRPassesYesAndExtractsUrl()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr create --source-branch feature/x --target-branch main --title fix: things --description body text --yes"] = new(
                Stdout: "Creating merge request...\nhttps://gitlab.example.com/group/project/-/merge_requests/42\n"),
        });

        var pr = await host.CreatePRAsync(
            "feature/x", "main", new PullRequestContent("fix: things", "body text"));

        Assert.Equal("42", pr.Number);
        Assert.Equal("https://gitlab.example.com/group/project/-/merge_requests/42", pr.Url);
    }

    [Fact]
    public async Task UpdatePRResolvesNumberFromUrlWhenIidMissing()
    {
        // Ports the update half of Go's
        // TestFindPRWithoutIIDKeepsNumberEmptyAndUpdatesByNumberFromURL: an MR
        // known only by URL is updated by the number extracted from that URL.
        var url = "https://gitlab.example.com/group/project/-/merge_requests/42";
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr update 42 --title updated --description body --yes"] = new(Stdout: "updated\n"),
        });

        var pr = new PullRequest { Number = "", Url = url };
        var updated = await host.UpdatePRAsync(pr, new PullRequestContent("updated", "body"));

        Assert.Same(pr, updated);
    }

    [Fact]
    public async Task GetChecksFallbackParsesMRJSONAfterPreamble()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr view 123 --output json"] = new(
                Stdout: "notice\n{\"head_pipeline\":{\"id\":77}}\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stdout: """[{"name":"test","status":"success"}]""" + "\n"),
        });

        var checks = await host.GetChecksFallbackAsync(new PullRequest { Number = "123" });

        var check = Assert.Single(checks);
        Assert.Equal("test", check.Name);
        Assert.Equal(CheckBucket.Pass, check.Bucket);
    }

    [Fact]
    public async Task GetChecksReturnsFallbackErrorOnInvalidMrJson()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(Stdout: "notice\nnot json\n"),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.GetChecksAsync(new PullRequest { Number = "123" }));

        Assert.Contains("invalid JSON output", ex.Message);
    }

    [Fact]
    public async Task GetChecksReturnsFallbackErrorWhenPipelineJobsFetchFails()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stderr: "gitlab unavailable\n", Code: 1),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.GetChecksAsync(new PullRequest { Number = "123" }));

        Assert.Contains("glab pipeline jobs", ex.Message);
    }

    [Fact]
    public async Task GetChecksReturnsPrimaryStatusErrorWhenMRFlagIsSupported()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "gitlab unavailable\n", Code: 1),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.GetChecksAsync(new PullRequest { Number = "123" }));

        Assert.Contains("glab ci status", ex.Message);
    }

    [Fact]
    public async Task GetChecksFallsBackForVariantUnsupportedMRFlagErrors()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(
                Stderr: "error: unrecognized arguments: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stdout: """[{"name":"test","status":"success"}]""" + "\n"),
        });

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        var check = Assert.Single(checks);
        Assert.Equal("test", check.Name);
        Assert.Equal(CheckBucket.Pass, check.Bucket);
    }

    [Fact]
    public async Task GetChecksFallbackRequestsJobDetails()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stdout: """{"jobs":[{"name":"lint","status":"failed"}]}""" + "\n"),
        });

        var checks = await host.GetChecksFallbackAsync(new PullRequest { Number = "123" });

        var check = Assert.Single(checks);
        Assert.Equal("lint", check.Name);
        Assert.Equal(CheckBucket.Fail, check.Bucket);
    }

    [Fact]
    public async Task FetchFailedCheckLogsRequestsJobDetails()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stdout: """{"jobs":[{"id":55,"name":"lint","status":"failed"}]}""" + "\n"),
            ["glab ci trace 55"] = new(Stdout: "lint failed\n"),
        });

        var logs = await host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "123" }, "", "", new[] { "lint" });

        Assert.Equal("lint failed", logs);
    }

    [Fact]
    public async Task FetchFailedCheckLogsParsesMRJSONAfterPreamble()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab mr view 123 --output json"] = new(
                Stdout: "notice\n{\"head_pipeline\":{\"id\":77}}\n"),
            ["glab ci get --pipeline-id 77 --output json --with-job-details"] = new(
                Stdout: """[{"id":55,"name":"lint","status":"failed"}]""" + "\n"),
            ["glab ci trace 55"] = new(Stdout: "lint failed\n"),
        });

        var logs = await host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "123" }, "", "", new[] { "lint" });

        Assert.Equal("lint failed", logs);
    }

    [Fact]
    public void GitlabStatusBucketTreatsManualJobsAsSkipped()
    {
        Assert.Equal(CheckBucket.Skipping, GitLabHost.GitlabStatusBucket("manual"));
    }

    [Fact]
    public async Task GetChecksReadsJobsViaAPIWhenProjectPathKnown()
    {
        // With a project path, pipeline jobs are read via `glab api` (REST),
        // which is branch-independent and works in the daemon's detached-HEAD
        // worktree. finished_at must be captured into CompletedAt.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab api --paginate projects/group%2Fproject/pipelines/77/jobs"] = new(
                Stdout: """[{"id":9,"name":"test","status":"success","finished_at":"2026-04-24T04:15:00.000Z"}]""" + "\n"),
        }, host: "gitlab.example.com", projectPath: "group/project");

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        var check = Assert.Single(checks);
        Assert.Equal("test", check.Name);
        Assert.Equal(CheckBucket.Pass, check.Bucket);
        Assert.Equal(new DateTimeOffset(2026, 4, 24, 4, 15, 0, TimeSpan.Zero), check.CompletedAt);
    }

    [Fact]
    public async Task GetChecksLeavesCompletedAtNullWhenFinishedAtMissingOrInvalid()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab api --paginate projects/group%2Fproject/pipelines/77/jobs"] = new(
                Stdout: """[{"name":"running","status":"running"},{"name":"bad","status":"success","finished_at":"not-a-time"}]""" + "\n"),
        }, projectPath: "group/project");

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Null(c.CompletedAt));
    }

    [Fact]
    public async Task GetChecksPaginatesJobsAcrossConcatenatedPages()
    {
        // `glab api --paginate` walks every page and writes one JSON array
        // per page, so the output is several arrays concatenated back to
        // back. The parser must read all of them; otherwise a failed job on a
        // later page is silently dropped and the CI verdict misses it. The
        // fixture key also asserts that the --paginate flag is actually
        // present on the jobs call.
        var page1 = """[{"id":1,"name":"build","status":"success"}]""";
        var page2 = """[{"id":2,"name":"deploy","status":"failed"}]""";
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab api --paginate projects/group%2Fproject/pipelines/77/jobs"] = new(
                Stdout: page1 + "\n" + page2 + "\n"),
        }, projectPath: "group/project");

        var checks = await host.GetChecksAsync(new PullRequest { Number = "123" });

        Assert.Equal(2, checks.Count);
        Assert.Contains(checks, c => c.Name == "deploy" && c.Bucket == CheckBucket.Fail);
    }

    [Fact]
    public void FindFailedJobIdScansConcatenatedPages()
    {
        // The failed job lives on the second concatenated page; FindFailedJobId
        // must still locate it across paginated output.
        var output = """[{"id":1,"name":"build","status":"success"}]""" + "\n"
            + """[{"id":2,"name":"deploy","status":"failed"}]""" + "\n";

        Assert.Equal(2, GitLabHost.FindFailedJobId(output, new[] { "deploy" }));
    }

    [Fact]
    public void ParseGitlabJobsSurfacesCorruptPayload()
    {
        // A wholly-malformed payload must surface a decode error rather than
        // be mistaken for an empty (no-jobs) result.
        var (_, wholeError) = GitLabHost.ParseGitlabJobs("""[{"id":1""");
        Assert.NotNull(wholeError);

        // When a good page parses before a corrupt one, the parsed jobs are
        // still returned, but the decode error must surface too: a failed job
        // on the dropped page would otherwise be silently hidden and read as
        // green.
        var output = """[{"id":1,"name":"build","status":"success"}]""" + "\n" + """[{"id":2""";
        var (checks, error) = GitLabHost.ParseGitlabJobs(output);
        Assert.NotNull(error);
        var check = Assert.Single(checks);
        Assert.Equal("build", check.Name);
    }

    [Fact]
    public async Task GetChecksSurfacesErrorWhenPaginatedPageIsCorrupt()
    {
        // End-to-end through GetChecks: a corrupt later page of paginated
        // `glab api` output must fail the call rather than return a partial
        // (potentially all-green) slice that hides a failed job on the
        // dropped page.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["glab ci status --mr 123 --output json"] = new(Stderr: "unknown flag: --mr\n", Code: 1),
            ["glab mr view 123 --output json"] = new(
                Stdout: """{"head_pipeline":{"id":77}}""" + "\n"),
            ["glab api --paginate projects/group%2Fproject/pipelines/77/jobs"] = new(
                Stdout: """[{"id":1,"name":"build","status":"success"}]""" + "\n" + """[{"id":2"""),
        }, projectPath: "group/project");

        await Assert.ThrowsAsync<ScmCommandException>(
            () => host.GetChecksAsync(new PullRequest { Number = "123" }));
    }
}
