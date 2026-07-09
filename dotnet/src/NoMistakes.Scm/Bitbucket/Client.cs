using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Scm.Bitbucket;

/// <summary>
/// A Bitbucket Cloud repository, addressed as workspace/repo_slug.
/// Mirrors Go's <c>internal/bitbucket.RepoRef</c>.
/// </summary>
public readonly record struct RepoRef(string Workspace, string RepoSlug);

/// <summary>
/// A Bitbucket pull request as returned by the REST API. Named with the
/// provider prefix because <see cref="NoMistakes.Scm.PullRequest"/> already
/// owns the bare name in consuming code. Mirrors Go's
/// <c>internal/bitbucket.PullRequest</c>.
/// </summary>
public sealed record BitbucketPullRequest(int Id, string Url, string State, string SourceCommitHash);

/// <summary>A commit build status. Mirrors Go's <c>bitbucket.CommitStatus</c>.</summary>
public sealed record CommitStatus(
    string Name = "", string Key = "", string State = "", string Description = "", string Url = "");

/// <summary>A pipeline run. Mirrors Go's <c>bitbucket.Pipeline</c>.</summary>
public sealed record Pipeline(string Uuid);

/// <summary>
/// A pipeline step; ResultName flattens Go's nested state.result.name.
/// </summary>
public sealed record PipelineStep(string Uuid, string StateName = "", string ResultName = "");

/// <summary>
/// Bitbucket Cloud REST API client, mirroring Go's
/// <c>internal/bitbucket.Client</c>. Bitbucket has no supported CLI, so
/// unlike the gh/glab/az wrappers the transport is HTTP with app-password
/// (email + API token) basic auth from the environment. Errors surface as
/// <see cref="ScmCommandException"/> with Go-shaped messages
/// ("Bitbucket GET /2.0/...: status 400: ...").
/// </summary>
public sealed class Client
{
    internal const string DefaultApiBaseUrl = "https://api.bitbucket.org";
    internal const string EnvEmail = "NO_MISTAKES_BITBUCKET_EMAIL";
    internal const string EnvToken = "NO_MISTAKES_BITBUCKET_API_TOKEN";
    internal const string EnvApiBaseUrl = "NO_MISTAKES_BITBUCKET_API_BASE_URL";
    private const int MaxStepLogBytes = 32 * 1024;

    private readonly string _baseUrl;
    private readonly AuthenticationHeaderValue _auth;
    private readonly HttpClient _httpClient;

    internal Client(string baseUrl, string email, string token, HttpClient httpClient)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + token)));
        _httpClient = httpClient;
    }

    internal string BaseUrl => _baseUrl;

    /// <summary>
    /// Builds a client from NO_MISTAKES_BITBUCKET_* variables, preferring
    /// entries in <paramref name="env"/> ("KEY=VALUE" strings, like Go's
    /// <c>cmd.Env</c>) over the ambient process environment. Throws
    /// <see cref="InvalidOperationException"/> ("missing
    /// NO_MISTAKES_BITBUCKET_EMAIL") when a credential is absent - this is
    /// the Bitbucket auth check; there is no CLI to probe.
    /// </summary>
    public static Client FromEnv(IReadOnlyList<string>? env, HttpClient? httpClient = null)
    {
        var email = LookupEnv(env, EnvEmail);
        if (email.Trim().Length == 0)
        {
            throw new InvalidOperationException($"missing {EnvEmail}");
        }
        var token = LookupEnv(env, EnvToken);
        if (token.Trim().Length == 0)
        {
            throw new InvalidOperationException($"missing {EnvToken}");
        }
        var baseUrl = LookupEnv(env, EnvApiBaseUrl);
        if (baseUrl.Trim().Length == 0)
        {
            baseUrl = DefaultApiBaseUrl;
        }
        return new Client(
            baseUrl, email, token,
            httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>
    /// Parses a Bitbucket remote (https or scp-like ssh) into workspace and
    /// repo slug. Rejects lookalike hosts ("bitbucket.org.evil.example") -
    /// only bitbucket.org and its subdomains are accepted. Throws
    /// <see cref="ArgumentException"/> with Go's exact messages.
    /// </summary>
    public static RepoRef ParseRepoRef(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.EndsWith(".git", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^4];
        }

        const string scpPrefix = "git@bitbucket.org:";
        if (trimmed.StartsWith(scpPrefix, StringComparison.Ordinal))
        {
            return ParseRepoPath(trimmed[scpPrefix.Length..]);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            throw new ArgumentException($"parse bitbucket repo URL: {raw}");
        }
        var host = parsed.Host.ToLowerInvariant();
        if (host != "bitbucket.org" && !host.EndsWith(".bitbucket.org", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unsupported Bitbucket host \"{parsed.Host}\"");
        }
        return ParseRepoPath(parsed.AbsolutePath.TrimStart('/'));
    }

    private static RepoRef ParseRepoPath(string path)
    {
        var parts = path.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
        {
            throw new ArgumentException($"invalid Bitbucket repository path \"{path}\"");
        }
        return new RepoRef(parts[0], parts[1]);
    }

    public async Task<BitbucketPullRequest?> FindOpenPRBySourceBranchAsync(
        RepoRef repo, string branch, string destBranch, CancellationToken cancellationToken = default)
    {
        var clauses = new List<string>
        {
            $"source.branch.name=\"{branch}\"",
            $"source.repository.full_name=\"{repo.Workspace}/{repo.RepoSlug}\"",
        };
        if (destBranch.Trim().Length > 0)
        {
            clauses.Add($"destination.branch.name=\"{destBranch}\"");
        }
        clauses.Add("state=\"OPEN\"");
        var path = RepoPRPath(repo) + "?q=" + Uri.EscapeDataString(string.Join(" AND ", clauses));

        var response = await DoJsonAsync<PagedResponse<RawPullRequest>>(
            HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (response?.Values is null || response.Values.Count == 0)
        {
            return null;
        }
        return response.Values[0].ToPullRequest();
    }

    public async Task<BitbucketPullRequest> CreatePRAsync(
        RepoRef repo, string sourceBranch, string destBranch, string title, string body,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            title,
            description = body,
            source = new { branch = new { name = sourceBranch } },
            destination = new { branch = new { name = destBranch } },
        };
        var response = await DoJsonAsync<RawPullRequest>(
            HttpMethod.Post, RepoPRPath(repo), requestBody, cancellationToken).ConfigureAwait(false);
        return (response ?? new RawPullRequest()).ToPullRequest();
    }

    public async Task<BitbucketPullRequest> UpdatePRAsync(
        RepoRef repo, int prId, string title, string body, CancellationToken cancellationToken = default)
    {
        var requestBody = new { title, description = body };
        var response = await DoJsonAsync<RawPullRequest>(
            HttpMethod.Put, $"{RepoPRPath(repo)}/{prId}", requestBody, cancellationToken).ConfigureAwait(false);
        return (response ?? new RawPullRequest()).ToPullRequest();
    }

    public async Task<BitbucketPullRequest> GetPRAsync(
        RepoRef repo, int prId, CancellationToken cancellationToken = default)
    {
        var response = await DoJsonAsync<RawPullRequest>(
            HttpMethod.Get, $"{RepoPRPath(repo)}/{prId}", null, cancellationToken).ConfigureAwait(false);
        return (response ?? new RawPullRequest()).ToPullRequest();
    }

    public async Task<IReadOnlyList<CommitStatus>> ListPRStatusesAsync(
        RepoRef repo, int prId, CancellationToken cancellationToken = default)
    {
        var next = $"{RepoPRPath(repo)}/{prId}/statuses?sort={Uri.EscapeDataString("-created_on")}";
        var statuses = new List<CommitStatus>();
        while (next.Length > 0)
        {
            var response = await DoJsonAsync<PagedResponse<RawCommitStatus>>(
                HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            foreach (var raw in response?.Values ?? new List<RawCommitStatus>())
            {
                statuses.Add(raw.ToStatus());
            }
            next = NextPage(response?.Next);
        }
        return statuses;
    }

    public async Task<IReadOnlyList<Pipeline>> ListPipelinesByCommitAsync(
        RepoRef repo, string commitSha, CancellationToken cancellationToken = default)
    {
        var next = $"/2.0/repositories/{repo.Workspace}/{repo.RepoSlug}/pipelines"
            + $"?{Uri.EscapeDataString("target.commit.hash")}={Uri.EscapeDataString(commitSha)}"
            + $"&sort={Uri.EscapeDataString("-created_on")}";
        var pipelines = new List<Pipeline>();
        while (next.Length > 0)
        {
            var response = await DoJsonAsync<PagedResponse<RawPipeline>>(
                HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            foreach (var raw in response?.Values ?? new List<RawPipeline>())
            {
                pipelines.Add(new Pipeline(raw.Uuid ?? ""));
            }
            next = NextPage(response?.Next);
        }
        return pipelines;
    }

    public async Task<IReadOnlyList<PipelineStep>> ListPipelineStepsAsync(
        RepoRef repo, string pipelineUuid, CancellationToken cancellationToken = default)
    {
        var next = $"/2.0/repositories/{repo.Workspace}/{repo.RepoSlug}/pipelines/{pipelineUuid}/steps";
        var steps = new List<PipelineStep>();
        while (next.Length > 0)
        {
            var response = await DoJsonAsync<PagedResponse<RawPipelineStep>>(
                HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            foreach (var raw in response?.Values ?? new List<RawPipelineStep>())
            {
                steps.Add(new PipelineStep(
                    raw.Uuid ?? "",
                    raw.State?.Name ?? "",
                    raw.State?.Result?.Name ?? ""));
            }
            next = NextPage(response?.Next);
        }
        return steps;
    }

    /// <summary>
    /// Fetches a pipeline step's log, keeping only the final 32 KiB - CI
    /// failures live at the tail, and whole logs can be arbitrarily large.
    /// </summary>
    public async Task<string> GetStepLogAsync(
        RepoRef repo, string pipelineUuid, string stepUuid, CancellationToken cancellationToken = default)
    {
        var path = $"/2.0/repositories/{repo.Workspace}/{repo.RepoSlug}/pipelines/{pipelineUuid}/steps/{stepUuid}/log";
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.Authorization = _auth;
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is < 200 or >= 300)
        {
            var data = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ScmCommandException(
                $"Bitbucket GET {path}: status {(int)response.StatusCode}: {data.Trim()}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var tail = await ReadTailAsync(stream, MaxStepLogBytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(tail).Trim();
    }

    /// <summary>
    /// Reads a stream to EOF keeping only the last <paramref name="maxBytes"/>
    /// bytes. Mirrors Go's <c>readTail</c>.
    /// </summary>
    internal static async Task<byte[]> ReadTailAsync(Stream r, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            return Array.Empty<byte>();
        }
        var buf = new List<byte>(maxBytes);
        var tmp = new byte[4096];
        while (true)
        {
            var n = await r.ReadAsync(tmp, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            if (n >= maxBytes)
            {
                buf.Clear();
                buf.AddRange(new ArraySegment<byte>(tmp, n - maxBytes, maxBytes));
                continue;
            }
            var overflow = buf.Count + n - maxBytes;
            if (overflow > 0)
            {
                buf.RemoveRange(0, overflow);
            }
            buf.AddRange(new ArraySegment<byte>(tmp, 0, n));
        }
        return buf.ToArray();
    }

    /// <summary>
    /// Guards pagination "next" links: relative URLs pass, and an absolute
    /// URL must share the configured base's scheme and host so a compromised
    /// or buggy response cannot redirect credentialed requests off-origin.
    /// </summary>
    internal string ValidatePaginationUrl(string rawUrl)
    {
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ScmCommandException($"parse Bitbucket base URL: {_baseUrl}");
        }
        if (!Uri.TryCreate(rawUrl, UriKind.RelativeOrAbsolute, out var nextUri))
        {
            throw new ScmCommandException($"parse Bitbucket pagination URL: {rawUrl}");
        }
        if (!nextUri.IsAbsoluteUri)
        {
            return rawUrl;
        }
        if (!string.Equals(nextUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(nextUri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScmCommandException($"reject cross-origin Bitbucket pagination URL \"{rawUrl}\"");
        }
        return rawUrl;
    }

    private string NextPage(string? next)
    {
        var trimmed = next ?? "";
        return trimmed.Length == 0 ? "" : ValidatePaginationUrl(trimmed);
    }

    private async Task<T?> DoJsonAsync<T>(
        HttpMethod method, string pathOrUrl, object? requestBody, CancellationToken cancellationToken)
        where T : class
    {
        var endpoint = pathOrUrl;
        if (!pathOrUrl.StartsWith("http://", StringComparison.Ordinal)
            && !pathOrUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            endpoint = _baseUrl + pathOrUrl;
        }
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = _auth;
        if (requestBody is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ScmCommandException($"Bitbucket {method.Method} {pathOrUrl}: {e.Message}");
        }
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                throw new ScmCommandException(
                    $"Bitbucket {method.Method} {pathOrUrl}: status {(int)response.StatusCode}: {payload.Trim()}");
            }
            try
            {
                return JsonSerializer.Deserialize<T>(payload);
            }
            catch (JsonException e)
            {
                throw new ScmCommandException($"decode Bitbucket response: {e.Message}");
            }
        }
    }

    private static string RepoPRPath(RepoRef repo)
        => $"/2.0/repositories/{repo.Workspace}/{repo.RepoSlug}/pullrequests";

    private static string LookupEnv(IReadOnlyList<string>? env, string key)
    {
        var prefix = key + "=";
        foreach (var entry in env ?? Array.Empty<string>())
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..];
            }
        }
        return Environment.GetEnvironmentVariable(key) ?? "";
    }

    private sealed record PagedResponse<T>(
        [property: JsonPropertyName("values")] List<T>? Values,
        [property: JsonPropertyName("next")] string? Next);

    private sealed record RawPullRequest(
        [property: JsonPropertyName("id")] int Id = 0,
        [property: JsonPropertyName("state")] string? State = null,
        [property: JsonPropertyName("source")] RawPRSource? Source = null,
        [property: JsonPropertyName("links")] RawPRLinks? Links = null)
    {
        public BitbucketPullRequest ToPullRequest() => new(
            Id,
            (Links?.Html?.Href ?? "").Trim(),
            (State ?? "").Trim(),
            (Source?.Commit?.Hash ?? "").Trim());
    }

    private sealed record RawPRSource([property: JsonPropertyName("commit")] RawPRCommit? Commit);

    private sealed record RawPRCommit([property: JsonPropertyName("hash")] string? Hash);

    private sealed record RawPRLinks([property: JsonPropertyName("html")] RawPRHtml? Html);

    private sealed record RawPRHtml([property: JsonPropertyName("href")] string? Href);

    private sealed record RawCommitStatus(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("url")] string? Url)
    {
        public CommitStatus ToStatus()
            => new(Name ?? "", Key ?? "", State ?? "", Description ?? "", Url ?? "");
    }

    private sealed record RawPipeline([property: JsonPropertyName("uuid")] string? Uuid);

    private sealed record RawPipelineStep(
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("state")] RawStepState? State);

    private sealed record RawStepState(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("result")] RawStepResult? Result);

    private sealed record RawStepResult([property: JsonPropertyName("name")] string? Name);
}
