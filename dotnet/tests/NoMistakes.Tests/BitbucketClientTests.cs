using System.Net;
using System.Text;
using NoMistakes.Scm;
using NoMistakes.Scm.Bitbucket;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/bitbucket client tests: repo-ref parsing (lookalike
/// host rejection), request path/query construction, pagination following
/// with cross-origin rejection, step-log tail capping, and the env-based
/// credential loading that is Bitbucket's auth check.
/// </summary>
public class BitbucketClientTests
{
    private const string BaseUrl = "https://api.test.local";
    private static readonly RepoRef Repo = new("test", "repo");

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static Client NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(BaseUrl, "test@example.com", "token", new HttpClient(new FakeHttpHandler(respond)));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string DecodedPath(HttpRequestMessage request)
        => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

    private static string QueryValue(HttpRequestMessage request, string key)
    {
        var query = request.RequestUri!.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var k = idx >= 0 ? pair[..idx] : pair;
            if (Uri.UnescapeDataString(k) == key)
            {
                return Uri.UnescapeDataString(idx >= 0 ? pair[(idx + 1)..] : "");
            }
        }
        return "";
    }

    [Theory]
    [InlineData("https://bitbucket.org/workspace/repo.git")]
    [InlineData("https://foo.bitbucket.org/workspace/repo.git")]
    [InlineData("git@bitbucket.org:workspace/repo.git")]
    public void ParseRepoRefAcceptsBitbucketHosts(string raw)
    {
        Assert.Equal(new RepoRef("workspace", "repo"), Client.ParseRepoRef(raw));
    }

    [Fact]
    public void ParseRepoRefRejectsLookalikeHost()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Client.ParseRepoRef("https://bitbucket.org.evil.example/workspace/repo.git"));
        Assert.Equal("unsupported Bitbucket host \"bitbucket.org.evil.example\"", ex.Message);
    }

    [Fact]
    public void ParseRepoRefRejectsShortPath()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Client.ParseRepoRef("https://bitbucket.org/workspace"));
        Assert.Equal("invalid Bitbucket repository path \"workspace\"", ex.Message);
    }

    [Fact]
    public async Task ListPRStatusesFollowsPagination()
    {
        var pageCalls = 0;
        var client = NewClient(request =>
        {
            Assert.Equal("/2.0/repositories/test/repo/pullrequests/42/statuses", DecodedPath(request));
            Assert.Equal("-created_on", QueryValue(request, "sort"));
            pageCalls++;
            return QueryValue(request, "page") switch
            {
                "" or "1" => Json(
                    """{"values":[{"name":"build","state":"SUCCESSFUL"}],"next":""" +
                    $"\"{BaseUrl}/2.0/repositories/test/repo/pullrequests/42/statuses?sort=-created_on&page=2\"}}"),
                "2" => Json("""{"values":[{"name":"tests","state":"FAILED"}]}"""),
                _ => throw new InvalidOperationException("unexpected page " + request.RequestUri),
            };
        });

        var statuses = await client.ListPRStatusesAsync(Repo, 42);

        Assert.Equal(2, statuses.Count);
        Assert.Equal("build", statuses[0].Name);
        Assert.Equal("tests", statuses[1].Name);
        Assert.Equal(2, pageCalls);
    }

    [Fact]
    public async Task ListPRStatusesRejectsCrossOriginPagination()
    {
        var client = NewClient(_ => Json(
            "{\"values\":[{\"name\":\"build\",\"state\":\"SUCCESSFUL\"}],"
            + "\"next\":\"https://evil.example/2.0/repositories/test/repo/pullrequests/42/statuses?page=2\"}"));

        var ex = await Assert.ThrowsAsync<ScmCommandException>(() => client.ListPRStatusesAsync(Repo, 42));
        Assert.Contains("cross-origin", ex.Message);
    }

    [Fact]
    public async Task FindOpenPRBySourceAndDestinationBranchFiltersSourceRepo()
    {
        var gotQ = "";
        var client = NewClient(request =>
        {
            Assert.Equal("/2.0/repositories/test/repo/pullrequests", DecodedPath(request));
            gotQ = QueryValue(request, "q");
            return Json(
                """{"values":[{"id":42,"links":{"html":{"href":"https://bitbucket.org/test/repo/pull-requests/42"}}}]}""");
        });

        var pr = await client.FindOpenPRBySourceBranchAsync(Repo, "feature", "main");

        Assert.NotNull(pr);
        Assert.Equal(42, pr!.Id);
        Assert.Equal(
            "source.branch.name=\"feature\" AND source.repository.full_name=\"test/repo\""
            + " AND destination.branch.name=\"main\" AND state=\"OPEN\"",
            gotQ);
    }

    [Fact]
    public async Task ListPipelinesByCommitFollowsPagination()
    {
        var pageCalls = 0;
        var client = NewClient(request =>
        {
            Assert.Equal("/2.0/repositories/test/repo/pipelines", DecodedPath(request));
            Assert.Equal("abc123", QueryValue(request, "target.commit.hash"));
            Assert.Equal("-created_on", QueryValue(request, "sort"));
            pageCalls++;
            return QueryValue(request, "page") switch
            {
                "" or "1" => Json(
                    """{"values":[{"uuid":"{first}"}],"next":""" +
                    $"\"{BaseUrl}/2.0/repositories/test/repo/pipelines?target.commit.hash=abc123&sort=-created_on&page=2\"}}"),
                "2" => Json("""{"values":[{"uuid":"{second}"}]}"""),
                _ => throw new InvalidOperationException("unexpected page " + request.RequestUri),
            };
        });

        var pipelines = await client.ListPipelinesByCommitAsync(Repo, "abc123");

        Assert.Equal(2, pipelines.Count);
        Assert.Equal("{first}", pipelines[0].Uuid);
        Assert.Equal("{second}", pipelines[1].Uuid);
        Assert.Equal(2, pageCalls);
    }

    [Fact]
    public async Task ListPipelineStepsFollowsPagination()
    {
        var pageCalls = 0;
        var client = NewClient(request =>
        {
            Assert.Equal("/2.0/repositories/test/repo/pipelines/{pipe}/steps", DecodedPath(request));
            pageCalls++;
            return QueryValue(request, "page") switch
            {
                "" or "1" => Json(
                    """{"values":[{"uuid":"{step-1}"}],"next":""" +
                    $"\"{BaseUrl}/2.0/repositories/test/repo/pipelines/%7Bpipe%7D/steps?page=2\"}}"),
                "2" => Json("""{"values":[{"uuid":"{step-2}","state":{"result":{"name":"FAILED"}}}]}"""),
                _ => throw new InvalidOperationException("unexpected page " + request.RequestUri),
            };
        });

        var steps = await client.ListPipelineStepsAsync(Repo, "{pipe}");

        Assert.Equal(2, steps.Count);
        Assert.Equal("{step-1}", steps[0].Uuid);
        Assert.Equal("{step-2}", steps[1].Uuid);
        Assert.Equal("FAILED", steps[1].ResultName);
        Assert.Equal(2, pageCalls);
    }

    [Fact]
    public async Task GetStepLogCapsResponseToTail()
    {
        const int maxLogBytes = 32 * 1024;
        var prefix = new string('a', 4096);
        var tail = new string('z', maxLogBytes);
        var client = NewClient(request =>
        {
            Assert.Equal(
                "/2.0/repositories/test/repo/pipelines/{pipe}/steps/{step}/log", DecodedPath(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(prefix + tail, Encoding.UTF8, "text/plain"),
            };
        });

        var logOutput = await client.GetStepLogAsync(Repo, "{pipe}", "{step}");

        Assert.Equal(maxLogBytes, logOutput.Length);
        Assert.Equal(tail, logOutput);
    }

    [Fact]
    public async Task GetStepLogSurfacesHttpError()
    {
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("no such step"),
        });

        var ex = await Assert.ThrowsAsync<ScmCommandException>(
            () => client.GetStepLogAsync(Repo, "{pipe}", "{step}"));
        Assert.Contains("status 404: no such step", ex.Message);
    }

    [Fact]
    public async Task CreatePRPostsToPullRequestsPath()
    {
        var body = "";
        var client = NewClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/2.0/repositories/test/repo/pullrequests", DecodedPath(request));
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"id":7,"state":"OPEN","links":{"html":{"href":"https://bitbucket.org/test/repo/pull-requests/7"}}}""");
        });

        var pr = await client.CreatePRAsync(Repo, "feature", "main", "T", "B");

        Assert.Equal(7, pr.Id);
        Assert.Equal("https://bitbucket.org/test/repo/pull-requests/7", pr.Url);
        Assert.Contains("\"title\":\"T\"", body);
        Assert.Contains("\"description\":\"B\"", body);
        Assert.Contains("\"source\":{\"branch\":{\"name\":\"feature\"}}", body);
        Assert.Contains("\"destination\":{\"branch\":{\"name\":\"main\"}}", body);
    }

    [Fact]
    public async Task UpdatePRPutsToPullRequestIdPath()
    {
        var client = NewClient(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/2.0/repositories/test/repo/pullrequests/42", DecodedPath(request));
            return Json("""{"id":42,"state":"OPEN"}""");
        });

        var pr = await client.UpdatePRAsync(Repo, 42, "T", "B");

        Assert.Equal(42, pr.Id);
        // No html link in the response: URL stays empty at the client layer
        // (the host constructs the fallback).
        Assert.Equal("", pr.Url);
    }

    // Env mutation is process-global; xunit runs tests within one class
    // serially and no other test class reads these vars, so save/restore
    // inside this class is safe (same reasoning as ProviderDetectorTests).
    private static void WithEnv(Action body)
    {
        var saved = new[] { Client.EnvEmail, Client.EnvToken, Client.EnvApiBaseUrl }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            body();
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public void FromEnvReadsAmbientEnvironment()
    {
        WithEnv(() =>
        {
            Environment.SetEnvironmentVariable(Client.EnvEmail, "ambient@example.com");
            Environment.SetEnvironmentVariable(Client.EnvToken, "ambient-token");
            Environment.SetEnvironmentVariable(Client.EnvApiBaseUrl, "https://ambient.example");

            var client = Client.FromEnv(null);

            Assert.Equal("https://ambient.example", client.BaseUrl);
        });
    }

    [Fact]
    public void FromEnvPrefersExplicitEnvironment()
    {
        WithEnv(() =>
        {
            Environment.SetEnvironmentVariable(Client.EnvEmail, "ambient@example.com");
            Environment.SetEnvironmentVariable(Client.EnvToken, "ambient-token");
            Environment.SetEnvironmentVariable(Client.EnvApiBaseUrl, "https://ambient.example");

            var client = Client.FromEnv(new[]
            {
                Client.EnvEmail + "=explicit@example.com",
                Client.EnvToken + "=explicit-token",
                Client.EnvApiBaseUrl + "=https://explicit.example",
            });

            Assert.Equal("https://explicit.example", client.BaseUrl);
        });
    }

    [Fact]
    public void FromEnvDefaultsBaseUrl()
    {
        WithEnv(() =>
        {
            Environment.SetEnvironmentVariable(Client.EnvEmail, null);
            Environment.SetEnvironmentVariable(Client.EnvToken, null);
            Environment.SetEnvironmentVariable(Client.EnvApiBaseUrl, null);

            var client = Client.FromEnv(new[]
            {
                Client.EnvEmail + "=e@example.com",
                Client.EnvToken + "=t",
            });

            Assert.Equal("https://api.bitbucket.org", client.BaseUrl);
        });
    }

    [Theory]
    [InlineData(Client.EnvEmail)]
    [InlineData(Client.EnvToken)]
    public void FromEnvRequiresCredentials(string missing)
    {
        WithEnv(() =>
        {
            Environment.SetEnvironmentVariable(Client.EnvEmail, null);
            Environment.SetEnvironmentVariable(Client.EnvToken, null);
            Environment.SetEnvironmentVariable(Client.EnvApiBaseUrl, null);
            var env = new List<string>();
            if (missing != Client.EnvEmail)
            {
                env.Add(Client.EnvEmail + "=e@example.com");
            }
            if (missing != Client.EnvToken)
            {
                env.Add(Client.EnvToken + "=t");
            }

            var ex = Assert.Throws<InvalidOperationException>(() => Client.FromEnv(env));
            Assert.Equal($"missing {missing}", ex.Message);
        });
    }
}
