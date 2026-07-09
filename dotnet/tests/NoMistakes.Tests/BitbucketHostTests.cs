using System.Net;
using System.Text;
using NoMistakes.Scm;
using NoMistakes.Scm.Bitbucket;
using Xunit;
using BitbucketHost = NoMistakes.Scm.Bitbucket.Host;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/bitbucket host tests: state/bucket normalization,
/// status naming and dedup, pipeline-UUID extraction from status URLs, and
/// the availability check (Bitbucket's auth lives in client construction, so
/// availability only verifies a configured client).
/// </summary>
public class BitbucketHostTests
{
    private static readonly RepoRef Repo = new("test", "repo");

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static Client NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new("https://api.test.local", "test@example.com", "token",
            new HttpClient(new FakeHttpHandler(respond)));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void ProviderAndCapabilities()
    {
        var host = new BitbucketHost(null, Repo);

        Assert.Equal(Provider.Bitbucket, host.Provider);
        Assert.False(host.Capabilities.MergeableState);
        Assert.True(host.Capabilities.FailedCheckLogs);
    }

    [Fact]
    public async Task AvailableReportsUnconfiguredClient()
    {
        Assert.Equal(
            "bitbucket client is not configured",
            await new BitbucketHost(null, Repo).CheckAvailabilityAsync());
    }

    [Fact]
    public async Task AvailableWithClient()
    {
        var host = new BitbucketHost(NewClient(_ => throw new InvalidOperationException("no HTTP expected")), Repo);

        Assert.Null(await host.CheckAvailabilityAsync());
    }

    [Fact]
    public async Task FindPRMapsClientLookupToPullRequest()
    {
        var host = new BitbucketHost(NewClient(request =>
        {
            Assert.Contains(
                "/2.0/repositories/test/repo/pullrequests",
                request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Json(
                """{"values":[{"id":42,"links":{"html":{"href":"https://bitbucket.org/test/repo/pull-requests/42"}}}]}""");
        }), Repo);

        var pr = await host.FindPRAsync("feature", "main");

        Assert.NotNull(pr);
        Assert.Equal("42", pr.Number);
        Assert.Equal("https://bitbucket.org/test/repo/pull-requests/42", pr.Url);
    }

    [Fact]
    public async Task FindPRReturnsNullWhenNoOpenPRExists()
    {
        var host = new BitbucketHost(NewClient(_ => Json("""{"values":[]}""")), Repo);

        Assert.Null(await host.FindPRAsync("feature", "main"));
    }

    [Fact]
    public async Task FindPRRequiresConfiguredClient()
    {
        var host = new BitbucketHost(null, Repo);

        var e = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.FindPRAsync("feature", "main"));

        Assert.Equal("bitbucket client is not configured", e.Message);
    }

    [Fact]
    public async Task GetMergeableStateUnsupported()
    {
        var host = new BitbucketHost(NewClient(_ => throw new InvalidOperationException("no HTTP expected")), Repo);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => host.GetMergeableStateAsync(new PullRequest { Number = "42" }));
    }

    [Fact]
    public async Task GetChecksDedupsAndFallsBackToConstructedUrl()
    {
        var host = new BitbucketHost(NewClient(request =>
        {
            Assert.Equal(
                "/2.0/repositories/test/repo/pullrequests/42/statuses",
                request.RequestUri!.AbsolutePath);
            // Newest-first: the duplicate "build" key must keep only the
            // first (newest) entry.
            return Json(
                "{\"values\":["
                + "{\"name\":\"build\",\"key\":\"build\",\"state\":\"SUCCESSFUL\"},"
                + "{\"name\":\"build\",\"key\":\"build\",\"state\":\"FAILED\"},"
                + "{\"key\":\"tests\",\"state\":\"INPROGRESS\"}"
                + "]}");
        }), Repo);

        var checks = await host.GetChecksAsync(new PullRequest { Number = "42" });

        Assert.Equal(2, checks.Count);
        Assert.Equal(new Check("build", CheckBucket.Pass), checks[0]);
        Assert.Equal(new Check("tests", CheckBucket.Pending), checks[1]);
    }

    [Fact]
    public async Task GetChecksRejectsNonNumericPRNumber()
    {
        var host = new BitbucketHost(NewClient(_ => throw new InvalidOperationException("no HTTP expected")), Repo);

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => host.GetChecksAsync(new PullRequest { Number = "abc" }));
        Assert.Equal("invalid Bitbucket PR number \"abc\"", ex.Message);
    }

    [Theory]
    [InlineData("OPEN", PullRequestState.Open)]
    [InlineData("open", PullRequestState.Open)]
    [InlineData("  OPEN  ", PullRequestState.Open)]
    [InlineData("MERGED", PullRequestState.Merged)]
    [InlineData("merged", PullRequestState.Merged)]
    [InlineData("DECLINED", PullRequestState.Closed)]
    [InlineData("declined", PullRequestState.Closed)]
    [InlineData("CLOSED", PullRequestState.Closed)]
    [InlineData("SUPERSEDED", PullRequestState.Closed)]
    // Go passes unknown states through raw; the enum's Unknown stands in and
    // matches no terminal state, so callers keep polling.
    [InlineData("DRAFT", PullRequestState.Unknown)]
    [InlineData("", PullRequestState.Unknown)]
    [InlineData("   ", PullRequestState.Unknown)]
    public void NormalizePRState(string raw, PullRequestState want)
    {
        Assert.Equal(want, BitbucketHost.NormalizePRState(raw));
    }

    [Theory]
    [InlineData("build", "build-key", "build")]
    [InlineData("", "build-key", "build-key")]
    [InlineData("   ", "build-key", "build-key")]
    [InlineData("  build  ", "", "build")]
    [InlineData("", "  build-key  ", "build-key")]
    [InlineData("", "", "")]
    [InlineData("  ", "  ", "")]
    public void StatusName(string name, string key, string want)
    {
        Assert.Equal(want, BitbucketHost.StatusName(new CommitStatus(Name: name, Key: key)));
    }

    [Theory]
    [InlineData("SUCCESSFUL", CheckBucket.Pass)]
    [InlineData("SUCCESS", CheckBucket.Pass)]
    [InlineData("successful", CheckBucket.Pass)]
    [InlineData("  SUCCESSFUL  ", CheckBucket.Pass)]
    [InlineData("FAILED", CheckBucket.Fail)]
    [InlineData("FAILURE", CheckBucket.Fail)]
    [InlineData("ERROR", CheckBucket.Fail)]
    [InlineData("failed", CheckBucket.Fail)]
    [InlineData("STOPPED", CheckBucket.Cancel)]
    [InlineData("INPROGRESS", CheckBucket.Pending)]
    [InlineData("IN_PROGRESS", CheckBucket.Pending)]
    [InlineData("PENDING", CheckBucket.Pending)]
    [InlineData("UNKNOWN", CheckBucket.None)]
    [InlineData("", CheckBucket.None)]
    [InlineData("   ", CheckBucket.None)]
    public void StatusBucket(string state, CheckBucket want)
    {
        Assert.Equal(want, BitbucketHost.StatusBucket(state));
    }

    [Theory]
    [InlineData("abc-def-123", "abc-def-123")]
    [InlineData("ABC-DEF-123", "abc-def-123")]
    [InlineData("{abc-def-123}", "abc-def-123")]
    [InlineData("{ABC-DEF}", "abc-def")]
    [InlineData("  {abc-def}  ", "abc-def")]
    // Trim with a {} cutset strips all leading/trailing braces, not one pair.
    [InlineData("{{abc-def}}", "abc-def")]
    [InlineData("{abc-def", "abc-def")]
    [InlineData("abc-def}}}", "abc-def")]
    [InlineData("a{b}c", "a{b}c")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("{}", "")]
    [InlineData("  {}  ", "")]
    public void NormalizePipelineUuid(string raw, string want)
    {
        Assert.Equal(want, BitbucketHost.NormalizePipelineUuid(raw));
    }

    [Theory]
    [InlineData("https://bitbucket.org/ws/repo/pipelines/results/{abc-def-123}", "abc-def-123")]
    [InlineData("https://bitbucket.org/ws/repo/pipelines/results/abc-def", "abc-def")]
    [InlineData("https://bitbucket.org/ws/repo/pipelines/results/{ABC-DEF}", "abc-def")]
    [InlineData("https://bitbucket.org/ws/repo/pipelines/results/{abc-def}?tab=logs", "abc-def")]
    [InlineData("https://bitbucket.org/ws/repo/pipelines/results/{abc-def}/steps", "abc-def")]
    // Fragment consulted before path.
    [InlineData("https://bitbucket.org/ws/pipelines/results/path-uuid#/pipelines/results/frag-uuid", "frag-uuid")]
    [InlineData("https://bitbucket.org/results/early/results/late", "late")]
    [InlineData("https://bitbucket.org/ws/repo/pipelines", "")]
    [InlineData("https://bitbucket.org/ws/repo#/pipelines/results/{frag-only}", "frag-only")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("not-a-url", "")]
    // An invalid percent-escape fails Go's url.Parse outright; the helper
    // must return empty rather than surface a parse error.
    [InlineData("https://bitbucket.org/x/results/{abc}%xx", "")]
    public void PipelineUuidFromStatusUrl(string raw, string want)
    {
        Assert.Equal(want, BitbucketHost.PipelineUuidFromStatusUrl(raw));
    }

    private static string ResultsUrl(string uuid)
        => "https://bitbucket.org/ws/repo/pipelines/results/{" + uuid + "}";

    [Fact]
    public void FailedPipelineUuidsReturnsNullWithoutUsableInput()
    {
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            Array.Empty<CommitStatus>(), Array.Empty<string>()));
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            Array.Empty<CommitStatus>(), new[] { "  ", "" }));
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            Array.Empty<CommitStatus>(), new[] { "build" }));
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            new[] { new CommitStatus(Name: "lint", Url: ResultsUrl("lint-uuid")) }, new[] { "build" }));
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            new[] { new CommitStatus(Name: "build", Url: "https://bitbucket.org/ws/repo/build") },
            new[] { "build" }));
        Assert.Null(BitbucketHost.FailedPipelineUuids(
            new[] { new CommitStatus(Name: "build", Url: "") }, new[] { "build" }));
    }

    [Fact]
    public void FailedPipelineUuidsExtractsMatchingTargets()
    {
        var got = BitbucketHost.FailedPipelineUuids(
            new[]
            {
                new CommitStatus(Name: "build", Url: ResultsUrl("abc")),
                new CommitStatus(Name: "tests", Url: ResultsUrl("def")),
                new CommitStatus(Name: "lint", Url: ResultsUrl("ghi")),
            },
            new[] { "build", "tests", "nonexistent" });

        Assert.NotNull(got);
        Assert.Equal(new[] { "abc", "def" }, got!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void FailedPipelineUuidsCollapsesDuplicatesAndNormalizes()
    {
        var got = BitbucketHost.FailedPipelineUuids(
            new[]
            {
                new CommitStatus(Name: "build", Url: ResultsUrl("ABC-DEF")),
                new CommitStatus(Name: "tests", Url: ResultsUrl("abc-def")),
            },
            new[] { "  build  ", "tests" });

        Assert.NotNull(got);
        Assert.Equal(new[] { "abc-def" }, got!.ToArray());
    }

    [Fact]
    public void FailedPipelineUuidsMatchesByKeyAndDedupsBeforeCollection()
    {
        var byKey = BitbucketHost.FailedPipelineUuids(
            new[] { new CommitStatus(Key: "build", Url: ResultsUrl("abc")) }, new[] { "build" });
        Assert.NotNull(byKey);
        Assert.Equal(new[] { "abc" }, byKey!.ToArray());

        // LatestStatuses dedup happens before UUID collection: the stale
        // second "build" status must not contribute its UUID.
        var deduped = BitbucketHost.FailedPipelineUuids(
            new[]
            {
                new CommitStatus(Name: "build", Key: "build", Url: ResultsUrl("first")),
                new CommitStatus(Name: "build", Key: "build", Url: ResultsUrl("second")),
            },
            new[] { "build" });
        Assert.NotNull(deduped);
        Assert.Equal(new[] { "first" }, deduped!.ToArray());
    }

    [Theory]
    [InlineData(42, "https://bitbucket.org/api/given/url", "https://bitbucket.org/api/given/url")]
    [InlineData(42, "", "https://bitbucket.org/test/repo/pull-requests/42")]
    [InlineData(0, "", "")]
    public void PRUrlPrefersApiUrlThenConstructs(int id, string rawUrl, string want)
    {
        Assert.Equal(want, BitbucketHost.PRUrl(Repo, id, rawUrl));
    }

    [Fact]
    public async Task FetchFailedCheckLogsWalksPipelinesToFailedStepLog()
    {
        var log = "boom: tests failed";
        var host = new BitbucketHost(NewClient(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            return path switch
            {
                "/2.0/repositories/test/repo/pullrequests/42" =>
                    Json("{\"id\":42,\"source\":{\"commit\":{\"hash\":\"abc123\"}}}"),
                "/2.0/repositories/test/repo/pullrequests/42/statuses" =>
                    Json("{\"values\":[{\"name\":\"tests\",\"state\":\"FAILED\",\"url\":\""
                        + ResultsUrl("pipe-1") + "\"}]}"),
                "/2.0/repositories/test/repo/pipelines" =>
                    Json("{\"values\":[{\"uuid\":\"{pipe-1}\"}]}"),
                "/2.0/repositories/test/repo/pipelines/{pipe-1}/steps" =>
                    Json("{\"values\":[{\"uuid\":\"{step-ok}\",\"state\":{\"result\":{\"name\":\"SUCCESSFUL\"}}},"
                        + "{\"uuid\":\"{step-bad}\",\"state\":{\"result\":{\"name\":\"FAILED\"}}}]}"),
                "/2.0/repositories/test/repo/pipelines/{pipe-1}/steps/{step-bad}/log" =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(log) },
                _ => throw new InvalidOperationException("unexpected path: " + path),
            };
        }), Repo);

        var got = await host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "42" }, "feature", "stale-sha", new[] { "tests" });

        Assert.Equal(log, got);
    }

    [Fact]
    public async Task FetchFailedCheckLogsDegradesToEmptyOnLookupFailure()
    {
        var host = new BitbucketHost(NewClient(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            }), Repo);

        Assert.Equal("", await host.FetchFailedCheckLogsAsync(
            new PullRequest { Number = "42" }, "feature", "abc123", new[] { "tests" }));
    }
}
