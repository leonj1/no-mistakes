namespace NoMistakes.Scm.Bitbucket;

/// <summary>
/// Implements <see cref="IHost"/> for Bitbucket using the REST API client,
/// mirroring Go's <c>internal/bitbucket.Host</c>.
/// </summary>
public sealed class Host(Client? client, RepoRef repo) : IHost
{
    private readonly Client? _client = client;
    private readonly RepoRef _repo = repo;

    public Provider Provider => Provider.Bitbucket;

    /// <summary>
    /// Bitbucket's REST API does not expose a reliable merge-conflict probe,
    /// so MergeableState is off.
    /// </summary>
    public Capabilities Capabilities => new(MergeableState: false, FailedCheckLogs: true);

    /// <summary>
    /// The Bitbucket auth check happens at client construction
    /// (<see cref="Client.FromEnv"/> requires the NO_MISTAKES_BITBUCKET_*
    /// credentials); here only a configured client is verified, mirroring
    /// Go's <c>Available</c>.
    /// </summary>
    public Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_client is null ? "bitbucket client is not configured" : null);

    /// <summary>
    /// Returns the open PR for the source branch, or null when none exists.
    /// Mirrors Go's <c>FindPR</c>, backed by
    /// <see cref="Client.FindOpenPRBySourceBranchAsync"/>.
    /// </summary>
    public async Task<PullRequest?> FindPRAsync(
        string branch, string baseBranch, CancellationToken cancellationToken = default)
    {
        var pr = await RequireClient()
            .FindOpenPRBySourceBranchAsync(_repo, branch, baseBranch, cancellationToken)
            .ConfigureAwait(false);
        return pr is null ? null : ToPR(pr);
    }

    public async Task<PullRequest> CreatePRAsync(
        string branch, string baseBranch, PullRequestContent content,
        CancellationToken cancellationToken = default)
    {
        var pr = await RequireClient().CreatePRAsync(
            _repo, branch, baseBranch, content.Title, content.Body, cancellationToken).ConfigureAwait(false);
        return ToPR(pr);
    }

    public async Task<PullRequest> UpdatePRAsync(
        PullRequest pr, PullRequestContent content, CancellationToken cancellationToken = default)
    {
        var updated = await RequireClient().UpdatePRAsync(
            _repo, PRId(pr), content.Title, content.Body, cancellationToken).ConfigureAwait(false);
        return ToPR(updated);
    }

    public async Task<PullRequestState> GetPRStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var got = await RequireClient().GetPRAsync(_repo, PRId(pr), cancellationToken).ConfigureAwait(false);
        return NormalizePRState(got.State);
    }

    public async Task<IReadOnlyList<Check>> GetChecksAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var statuses = await RequireClient()
            .ListPRStatusesAsync(_repo, PRId(pr), cancellationToken).ConfigureAwait(false);
        var latest = LatestStatuses(statuses);
        var checks = new List<Check>(latest.Count);
        foreach (var status in latest)
        {
            checks.Add(new Check(StatusName(status), StatusBucket(status.State)));
        }
        return checks;
    }

    public Task<MergeableState> GetMergeableStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("operation not supported by this provider");

    /// <summary>
    /// Best-effort tail of the first failing pipeline step's log for the PR's
    /// head commit; every lookup failure degrades to "" (the CI monitor
    /// treats missing logs as merely less context, never an error).
    /// </summary>
    public async Task<string> FetchFailedCheckLogsAsync(
        PullRequest pr, string branch, string headSha, IReadOnlyList<string> failingNames,
        CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return "";
        }
        var id = PRId(pr);
        var commitSha = headSha.Trim();
        IReadOnlySet<string>? targets = null;
        try
        {
            var got = await _client.GetPRAsync(_repo, id, cancellationToken).ConfigureAwait(false);
            if (got.SourceCommitHash.Trim().Length > 0)
            {
                commitSha = got.SourceCommitHash.Trim();
            }
        }
        catch (ScmCommandException)
        {
        }
        try
        {
            var statuses = await _client.ListPRStatusesAsync(_repo, id, cancellationToken).ConfigureAwait(false);
            targets = FailedPipelineUuids(statuses, failingNames);
        }
        catch (ScmCommandException)
        {
        }
        if (commitSha.Length == 0)
        {
            return "";
        }
        IReadOnlyList<Pipeline> pipelines;
        try
        {
            pipelines = await _client
                .ListPipelinesByCommitAsync(_repo, commitSha, cancellationToken).ConfigureAwait(false);
        }
        catch (ScmCommandException)
        {
            return "";
        }
        foreach (var pipelineRun in pipelines)
        {
            if (targets is not null && targets.Count > 0
                && !targets.Contains(NormalizePipelineUuid(pipelineRun.Uuid)))
            {
                continue;
            }
            IReadOnlyList<PipelineStep> steps;
            try
            {
                steps = await _client
                    .ListPipelineStepsAsync(_repo, pipelineRun.Uuid, cancellationToken).ConfigureAwait(false);
            }
            catch (ScmCommandException)
            {
                continue;
            }
            foreach (var step in steps)
            {
                if (!string.Equals(step.ResultName, "FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string logOutput;
                try
                {
                    logOutput = await _client
                        .GetStepLogAsync(_repo, pipelineRun.Uuid, step.Uuid, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ScmCommandException)
                {
                    continue;
                }
                if (logOutput.Trim().Length > 0)
                {
                    return logOutput.Trim();
                }
            }
        }
        return "";
    }

    private Client RequireClient()
        => _client ?? throw new ScmCommandException("bitbucket client is not configured");

    private static int PRId(PullRequest pr)
        => int.TryParse(pr.Number, out var id)
            ? id
            : throw new ScmCommandException($"invalid Bitbucket PR number \"{pr.Number}\"");

    private PullRequest ToPR(BitbucketPullRequest pr) => new()
    {
        Number = pr.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Url = PRUrl(_repo, pr.Id, pr.Url),
    };

    internal static string PRUrl(RepoRef repo, int prId, string rawUrl)
    {
        var url = rawUrl.Trim();
        if (url.Length > 0)
        {
            return url;
        }
        if (prId <= 0 || repo.Workspace.Trim().Length == 0 || repo.RepoSlug.Trim().Length == 0)
        {
            return "";
        }
        return $"https://bitbucket.org/{repo.Workspace}/{repo.RepoSlug}/pull-requests/{prId}";
    }

    internal static PullRequestState NormalizePRState(string raw)
        => raw.Trim().ToUpperInvariant() switch
        {
            "OPEN" => PullRequestState.Open,
            "MERGED" => PullRequestState.Merged,
            "DECLINED" or "CLOSED" or "SUPERSEDED" => PullRequestState.Closed,
            // Go passes the raw string through verbatim; the enum's Unknown
            // stands in - it matches no terminal state, so callers keep
            // polling.
            _ => PullRequestState.Unknown,
        };

    /// <summary>
    /// Keeps only the newest status per unique key/name (Bitbucket reports
    /// them newest-first via sort=-created_on).
    /// </summary>
    internal static IReadOnlyList<CommitStatus> LatestStatuses(IReadOnlyList<CommitStatus> statuses)
    {
        var latest = new List<CommitStatus>(statuses.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            var id = status.Key.Trim();
            if (id.Length == 0)
            {
                id = StatusName(status);
            }
            if (id.Length == 0)
            {
                latest.Add(status);
                continue;
            }
            if (!seen.Add(id))
            {
                continue;
            }
            latest.Add(status);
        }
        return latest;
    }

    internal static string StatusName(CommitStatus status)
    {
        var name = status.Name.Trim();
        return name.Length > 0 ? name : status.Key.Trim();
    }

    internal static CheckBucket StatusBucket(string state)
        => state.Trim().ToUpperInvariant() switch
        {
            "SUCCESSFUL" or "SUCCESS" => CheckBucket.Pass,
            "FAILED" or "FAILURE" or "ERROR" => CheckBucket.Fail,
            "STOPPED" => CheckBucket.Cancel,
            "INPROGRESS" or "IN_PROGRESS" or "PENDING" => CheckBucket.Pending,
            _ => CheckBucket.None,
        };

    internal static string NormalizePipelineUuid(string raw)
        => raw.Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>
    /// Extracts the pipeline UUID from a commit status's target URL, which
    /// points at .../pipelines/results/{uuid}. The fragment is consulted
    /// before the path (Bitbucket's web URLs carry the route in the
    /// fragment). Returns "" for anything unparsable.
    /// </summary>
    internal static string PipelineUuidFromStatusUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || !HasValidPercentEscapes(trimmed))
        {
            // Go's url.Parse rejects invalid percent escapes outright; the
            // helper returns empty rather than surfacing the parse error.
            return "";
        }
        var fragment = "";
        var hash = trimmed.IndexOf('#');
        if (hash >= 0)
        {
            fragment = trimmed[(hash + 1)..];
            trimmed = trimmed[..hash];
        }
        // Path component: strip scheme://host; anything without a scheme is
        // treated as a bare path, matching Go's relative-URL parse.
        var path = trimmed;
        var schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var afterScheme = trimmed[(schemeIdx + 3)..];
            var slash = afterScheme.IndexOf('/');
            path = slash >= 0 ? afterScheme[slash..] : "";
        }
        foreach (var fragmentOrPath in new[] { fragment, path })
        {
            var decoded = Uri.UnescapeDataString(fragmentOrPath);
            var idx = decoded.LastIndexOf("/results/", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }
            var uuid = decoded[(idx + "/results/".Length)..];
            uuid = uuid.Split('?', 2)[0].Trim();
            uuid = uuid.Split('/', 2)[0].Trim();
            return NormalizePipelineUuid(uuid);
        }
        return "";
    }

    private static bool HasValidPercentEscapes(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '%')
            {
                continue;
            }
            if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Maps the failing check names back to the pipeline UUIDs their statuses
    /// point at, so log fetching can skip unrelated pipelines. Returns null
    /// when nothing maps (callers then consider every pipeline).
    /// </summary>
    internal static IReadOnlySet<string>? FailedPipelineUuids(
        IReadOnlyList<CommitStatus> statuses, IReadOnlyList<string> failingNames)
    {
        if (failingNames.Count == 0)
        {
            return null;
        }
        var failing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in failingNames)
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                failing.Add(trimmed);
            }
        }
        if (failing.Count == 0)
        {
            return null;
        }
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in LatestStatuses(statuses))
        {
            if (!failing.Contains(StatusName(status)))
            {
                continue;
            }
            var uuid = PipelineUuidFromStatusUrl(status.Url);
            if (uuid.Length > 0)
            {
                targets.Add(uuid);
            }
        }
        return targets.Count == 0 ? null : targets;
    }
}
