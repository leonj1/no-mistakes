using NoMistakes.Scm;
using Xunit;
using AzureDevOpsHost = NoMistakes.Scm.AzureDevOps.Host;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm/azuredevops tests for the az command wrapper:
/// argument construction (org/project/repo scoping, --output json), the
/// extension + auth availability probes, stdout-only JSON reads (stderr
/// chatter must not corrupt payloads), policy-evaluation filtering, and the
/// 4000-character description clamp. PR lookup (FindPR / `az repos pr list`)
/// lands in slice 6c.
/// </summary>
public class AzureDevOpsHostTests
{
    private const string TestOrg = "https://dev.azure.com/myorg";
    private const string TestProject = "myproject";
    private const string TestRepo = "myrepo";

    private static AzureDevOpsHost NewHost(
        IReadOnlyDictionary<string, FakeCommandResponse> responses,
        Func<bool>? cliAvailable = null)
        => new(ScmCommandFakes.Runner(responses), cliAvailable ?? (() => true), TestOrg, TestProject, TestRepo);

    [Fact]
    public void ProviderAndCapabilities()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>());

        Assert.Equal(Provider.AzureDevOps, host.Provider);
        Assert.True(host.Capabilities.MergeableState);
        Assert.False(host.Capabilities.FailedCheckLogs);
    }

    [Fact]
    public async Task AvailableChecksExtensionAndAuth()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az extension show --name azure-devops"] = new(Stdout: "{}\n"),
            ["az devops project list --query value[0].id --output tsv --organization " + TestOrg] =
                new(Stdout: "abc\n"),
        });

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsMissingCli()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>(), cliAvailable: () => false);

        Assert.Equal("az CLI is not installed", await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableReportsMissingExtension()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az extension show --name azure-devops"] = new(Stderr: "not installed\n", Code: 1),
        });

        var reason = await host.CheckAvailabilityAsync();
        Assert.NotNull(reason);
        Assert.Contains("azure-devops extension", reason);
    }

    [Fact]
    public async Task AvailableReportsUnauthenticated()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az extension show --name azure-devops"] = new(Stdout: "{}\n"),
            ["az devops project list --query value[0].id --output tsv --organization " + TestOrg] =
                new(Stderr: "TF400813: not authorized\n", Code: 1),
        });

        Assert.Equal("az CLI is not authenticated for Azure DevOps", await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task CreatePRConstructsBrowsableUrl()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr create --source-branch feature --target-branch main --title T --description B"
                + " --organization " + TestOrg + " --project " + TestProject + " --repository " + TestRepo
                + " --output json"] =
                // az returns an _apis/... url in the top-level field; it must
                // NOT be used.
                new(Stdout: """{"pullRequestId":7,"url":"https://dev.azure.com/myorg/_apis/git/repositories/abc/pullRequests/7"}""" + "\n"),
        });

        var pr = await host.CreatePRAsync("feature", "main", new PullRequestContent("T", "B"));

        Assert.Equal("7", pr.Number);
        Assert.Equal("https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/7", pr.Url);
    }

    [Fact]
    public async Task CreatePRIgnoresStderrChatter()
    {
        // az prints preview-command notices to stderr; only stdout may feed
        // the JSON decode.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr create --source-branch feature --target-branch main --title T --description B"
                + " --organization " + TestOrg + " --project " + TestProject + " --repository " + TestRepo
                + " --output json"] =
                new(
                    Stdout: """{"pullRequestId":7,"repository":{"webUrl":"https://dev.azure.com/myorg/myproject/_git/myrepo"}}""" + "\n",
                    Stderr: "Command group 'repos pr' is in preview and under development.\n"),
        });

        var pr = await host.CreatePRAsync("feature", "main", new PullRequestContent("T", "B"));

        Assert.Equal("7", pr.Number);
        Assert.Equal("https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/7", pr.Url);
    }

    [Fact]
    public async Task CreatePRTruncatesOverlongDescription()
    {
        // A body well over Azure DevOps' 4000-character description cap. az
        // rejects longer descriptions outright, so the wrapper must clamp.
        var body = new string('x', 5000);
        var clamped = PRBody.Clamp(body, PRBody.MaxChars(Provider.AzureDevOps));
        Assert.True(PRBody.Length(clamped) <= 4000);

        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr create --source-branch feature --target-branch main --title T --description " + clamped
                + " --organization " + TestOrg + " --project " + TestProject + " --repository " + TestRepo
                + " --output json"] =
                new(Stdout: """{"pullRequestId":7}""" + "\n"),
        });

        var pr = await host.CreatePRAsync("feature", "main", new PullRequestContent("T", body));

        Assert.Equal("7", pr.Number);
    }

    [Fact]
    public async Task CreatePRSurfacesStderrInError()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr create --source-branch feature --target-branch main --title T --description B"
                + " --organization " + TestOrg + " --project " + TestProject + " --repository " + TestRepo
                + " --output json"] =
                new(Stderr: "TF401019: not found\n", Code: 1),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.CreatePRAsync("feature", "main", new PullRequestContent("T", "B")));
        Assert.Equal("az repos pr create: TF401019: not found: exit status 1", ex.Message);
    }

    [Fact]
    public async Task CreatePRReportsParseError()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr create --source-branch feature --target-branch main --title T --description B"
                + " --organization " + TestOrg + " --project " + TestProject + " --repository " + TestRepo
                + " --output json"] =
                new(Stdout: "not json at all\n"),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.CreatePRAsync("feature", "main", new PullRequestContent("T", "B")));
        Assert.StartsWith("az repos pr create: parse response", ex.Message);
    }

    [Fact]
    public async Task UpdatePRUsesOrgScopeOnly()
    {
        // update/show/policy-list take only --organization: the PR id is
        // organization-unique and az rejects --project/--repository on them.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr update --id 42 --title T --description B --organization " + TestOrg + " --output json"] =
                new(Stdout: """{"pullRequestId":42}""" + "\n"),
        });
        var pr = new PullRequest { Number = "42", Url = "https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/42" };

        var updated = await host.UpdatePRAsync(pr, new PullRequestContent("T", "B"));

        Assert.Same(pr, updated);
    }

    [Fact]
    public async Task UpdatePRExtractsIdFromUrlWhenNumberMissing()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr update --id 42 --title T --description B --organization " + TestOrg + " --output json"] =
                new(Stdout: """{"pullRequestId":42}""" + "\n"),
        });
        var pr = new PullRequest { Url = "https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/42" };

        Assert.Same(pr, await host.UpdatePRAsync(pr, new PullRequestContent("T", "B")));
    }

    [Fact]
    public async Task UpdatePRRequiresId()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>());

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.UpdatePRAsync(new PullRequest(), new PullRequestContent("T", "B")));
        Assert.Equal("az repos pr update: missing PR id", ex.Message);
    }

    [Theory]
    [InlineData("active", PullRequestState.Open)]
    [InlineData("completed", PullRequestState.Merged)]
    [InlineData("abandoned", PullRequestState.Closed)]
    [InlineData("draft", PullRequestState.Unknown)]
    public async Task GetPRStateNormalizes(string raw, PullRequestState want)
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr show --id 42 --organization " + TestOrg + " --output json"] =
                new(Stdout: $$"""{"pullRequestId":42,"status":"{{raw}}"}""" + "\n"),
        });

        Assert.Equal(want, await host.GetPRStateAsync(new PullRequest { Number = "42" }));
    }

    [Theory]
    [InlineData("succeeded", MergeableState.Mergeable)]
    [InlineData("conflicts", MergeableState.Conflicting)]
    [InlineData("rejectedByPolicy", MergeableState.Pending)]
    [InlineData("failure", MergeableState.Pending)]
    [InlineData("queued", MergeableState.Pending)]
    [InlineData("notSet", MergeableState.Pending)]
    public async Task GetMergeableStateNormalizes(string raw, MergeableState want)
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr show --id 42 --organization " + TestOrg + " --output json"] =
                new(Stdout: $$"""{"pullRequestId":42,"mergeStatus":"{{raw}}"}""" + "\n"),
        });

        Assert.Equal(want, await host.GetMergeableStateAsync(new PullRequest { Number = "42" }));
    }

    [Fact]
    public async Task GetChecksMapsPolicyEvaluations()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr policy list --id 42 --organization " + TestOrg + " --output json"] = new(Stdout:
                "[" +
                """{"status":"approved","completedDate":"2026-04-24T04:15:00Z","configuration":{"type":{"displayName":"Build"},"settings":{"displayName":"Build validation"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Build"},"settings":{}},"context":{"buildDefinitionName":"ci-build"}},""" +
                """{"status":"running","configuration":{"type":{"displayName":"Status"}}},""" +
                """{"status":"notApplicable","configuration":{"type":{"displayName":"Required reviewers"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Minimum number of reviewers"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Comment requirements"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Require a merge strategy"}}}""" +
                "]\n"),
        });

        var checks = await host.GetChecksAsync(new PullRequest { Number = "42" });

        Assert.Equal(3, checks.Count);
        Assert.Equal(new Check(
            "Build validation", CheckBucket.Pass,
            new DateTimeOffset(2026, 4, 24, 4, 15, 0, TimeSpan.Zero)), checks[0]);
        Assert.Equal(new Check("ci-build", CheckBucket.Fail), checks[1]);
        Assert.Equal(new Check("Status", CheckBucket.Pending), checks[2]);
    }

    [Fact]
    public async Task GetChecksExcludesApprovalGatesOnHealthyPR()
    {
        // A normal open PR awaiting human review: every approval/merge gate
        // reports a blocking "rejected" status, but none is a CI failure.
        // GetChecks must return no checks so the CI monitor does not launch
        // pointless auto-fix attempts.
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr policy list --id 42 --organization " + TestOrg + " --output json"] = new(Stdout:
                "[" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Minimum number of reviewers"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Required reviewers"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Comment requirements"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Work item linking"}}},""" +
                """{"status":"rejected","configuration":{"type":{"displayName":"Require a merge strategy"}}}""" +
                "]\n"),
        });

        Assert.Empty(await host.GetChecksAsync(new PullRequest { Number = "42" }));
    }

    [Fact]
    public async Task GetChecksEmpty()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>
        {
            ["az repos pr policy list --id 42 --organization " + TestOrg + " --output json"] = new(Stdout: "[]\n"),
        });

        Assert.Empty(await host.GetChecksAsync(new PullRequest { Number = "42" }));
    }

    [Fact]
    public async Task FetchFailedCheckLogsUnsupported()
    {
        var host = NewHost(new Dictionary<string, FakeCommandResponse>());

        await Assert.ThrowsAsync<NotSupportedException>(() => host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "42" }, "feature", "abc123", new[] { "ci-build" }));
    }
}
