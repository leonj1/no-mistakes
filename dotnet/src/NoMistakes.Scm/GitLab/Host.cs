using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Scm.GitLab;

/// <summary>
/// Talks to GitLab through the glab CLI, mirroring Go's
/// <c>internal/scm/gitlab.Host</c>. The backend is pinned against glab v1.5x.
/// <paramref name="host"/> is the repo's GitLab hostname; when set the
/// availability check is scoped to it via --hostname so a stale credential
/// for an unrelated configured glab host cannot make this repo look
/// unauthenticated. <paramref name="projectPath"/> is the repo's
/// "group/project" path (subgroups allowed); when set, pipeline-job reads go
/// through `glab api` (REST), which is branch-independent and works in the
/// daemon's detached-HEAD worktree, where `glab ci get` refuses to run
/// without a current branch. Both are optional; empty reproduces the legacy
/// unscoped behavior.
/// </summary>
public sealed class Host(CommandRunner run, Func<bool>? cliAvailable, string host, string projectPath) : IHost
{
    private readonly CommandRunner _run = run;
    private readonly Func<bool>? _cliAvailable = cliAvailable;
    private readonly string _host = host.Trim();
    private readonly string _projectPath = projectPath.Trim();

    public Provider Provider => Provider.GitLab;

    public Capabilities Capabilities => new(MergeableState: true, FailedCheckLogs: true);

    /// <summary>
    /// Returns the glab invocation that lists a pipeline's jobs. With a known
    /// project path it uses `glab api` (branch-independent, works in a
    /// detached-HEAD worktree); otherwise it falls back to `glab ci get`,
    /// which needs a current branch.
    /// </summary>
    internal IReadOnlyList<string> PipelineJobsArgs(int pipelineId)
    {
        if (_projectPath.Length > 0)
        {
            // GitLab's REST API wants the project as a single URL-encoded
            // "group%2Fproject" path parameter. Escape each segment
            // defensively and rejoin with %2F so any reserved character in a
            // segment is encoded too, not just the separating slashes.
            var segments = _projectPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = Uri.EscapeDataString(segments[i]);
            }
            var enc = string.Join("%2F", segments);
            // --paginate walks every page; a pipeline with more jobs than fit
            // on one page (GitLab defaults to 20 per page) would otherwise
            // silently drop the jobs on later pages and the CI verdict could
            // miss a failed job. glab writes one JSON array per page, so the
            // parser handles concatenated docs.
            return new[]
            {
                "api", "--paginate",
                string.Create(CultureInfo.InvariantCulture, $"projects/{enc}/pipelines/{pipelineId}/jobs"),
            };
        }
        return new[]
        {
            "ci", "get",
            "--pipeline-id", pipelineId.ToString(CultureInfo.InvariantCulture),
            "--output", "json", "--with-job-details",
        };
    }

    public async Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (_cliAvailable is not null && !_cliAvailable())
        {
            return "glab CLI is not installed";
        }
        // Scope the auth check to this repo's host. Unscoped `glab auth
        // status` checks every configured instance and exits non-zero if ANY
        // of them has a stale/expired token, even when this repo's own host
        // is fully authenticated. Passing --hostname keeps an unrelated bad
        // credential from poisoning availability for this repo. When the host
        // is unknown we fall back to the unscoped check (fail-safe: same
        // behavior as before).
        var authArgs = new List<string> { "auth", "status" };
        if (_host.Length > 0)
        {
            authArgs.Add("--hostname");
            authArgs.Add(_host);
        }
        var result = await _run("glab", authArgs, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return "glab CLI is not authenticated";
        }
        return null;
    }

    public async Task<PullRequest> CreatePRAsync(
        string branch, string baseBranch, PullRequestContent content,
        CancellationToken cancellationToken = default)
    {
        var result = await _run("glab", new[]
        {
            "mr", "create",
            "--source-branch", branch,
            "--target-branch", baseBranch,
            "--title", content.Title,
            "--description", content.Body,
            "--yes",
        }, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"glab mr create: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        var url = ExtractMRUrl(result.CombinedOutput);
        return new PullRequest
        {
            Number = PullRequestUrl.TryExtractNumber(url, out var number) ? number : "",
            Url = url,
        };
    }

    public async Task<PullRequest> UpdatePRAsync(
        PullRequest pr, PullRequestContent content, CancellationToken cancellationToken = default)
    {
        var id = pr.Number;
        if (id.Length == 0 && PullRequestUrl.TryExtractNumber(pr.Url, out var fromUrl))
        {
            id = fromUrl;
        }
        if (id.Length == 0)
        {
            id = pr.Url;
        }
        var result = await _run("glab", new[]
        {
            "mr", "update", id,
            "--title", content.Title,
            "--description", content.Body,
            "--yes",
        }, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"glab mr update: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        return pr;
    }

    public async Task<PullRequestState> GetPRStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var mr = await ViewMRAsync(pr.Number, cancellationToken).ConfigureAwait(false);
        return NormalizePRState(mr.State ?? "");
    }

    public async Task<MergeableState> GetMergeableStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var mr = await ViewMRAsync(pr.Number, cancellationToken).ConfigureAwait(false);
        if (mr.HasConflicts)
        {
            return MergeableState.Conflicting;
        }
        // detailed_merge_status is preferred; merge_status is the legacy field.
        var status = (mr.DetailedMergeStatus ?? "").Trim().ToLowerInvariant();
        if (status.Length == 0)
        {
            status = (mr.MergeStatus ?? "").Trim().ToLowerInvariant();
        }
        return status switch
        {
            "mergeable" or "can_be_merged" => MergeableState.Mergeable,
            "broken_status" or "cannot_be_merged" => MergeableState.Conflicting,
            "checking" or "unchecked" or "ci_still_running" or "" => MergeableState.Pending,
            _ => MergeableState.Mergeable,
        };
    }

    private async Task<MrPayload> ViewMRAsync(string id, CancellationToken cancellationToken)
    {
        var result = await _run(
            "glab", new[] { "mr", "view", id, "--output", "json" }, null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"glab mr view: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        var mr = ParseMRPayload(result.CombinedOutput);
        if (mr is null)
        {
            throw new ScmCommandException(
                $"glab mr view: invalid JSON output: {result.CombinedOutput.Trim()}");
        }
        return mr;
    }

    public async Task<IReadOnlyList<Check>> GetChecksAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        // glab ci status --mr <id> --output json lists jobs for the MR's
        // latest pipeline. Not all glab versions support --mr; fall back to
        // listing pipelines by branch via view.
        var result = await _run(
            "glab", new[] { "ci", "status", "--mr", pr.Number, "--output", "json" },
            null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (!IsUnsupportedMRFlagError(result.CombinedOutput))
            {
                throw new ScmCommandException(
                    $"glab ci status: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
            }
            return await GetChecksFallbackAsync(pr, cancellationToken).ConfigureAwait(false);
        }
        return ChecksOrThrow(result.CombinedOutput);
    }

    internal static bool IsUnsupportedMRFlagError(string output)
    {
        var msg = output.Trim().ToLowerInvariant();
        if (!msg.Contains("--mr", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var marker in new[]
        {
            "unknown flag",
            "unknown option",
            "unsupported flag",
            "unsupported option",
            "unrecognized argument",
            "unrecognized arguments",
            "unrecognized option",
            "unknown argument",
            "unexpected argument",
            "flag provided but not defined",
        })
        {
            if (msg.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    internal async Task<IReadOnlyList<Check>> GetChecksFallbackAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        // Try fetching the MR's pipeline and listing its jobs.
        var pipelineId = await HeadPipelineIdAsync(pr, throwOnFailure: true, cancellationToken)
            .ConfigureAwait(false);
        if (pipelineId == 0)
        {
            return Array.Empty<Check>();
        }
        var jobsResult = await _run("glab", PipelineJobsArgs(pipelineId), null, cancellationToken)
            .ConfigureAwait(false);
        if (!jobsResult.Success)
        {
            throw new ScmCommandException(
                $"glab pipeline jobs: {jobsResult.CombinedOutput.Trim()}: exit status {jobsResult.ExitCode}");
        }
        return ChecksOrThrow(jobsResult.CombinedOutput);
    }

    public async Task<string> FetchFailedCheckLogsAsync(
        PullRequest pr, string branch, string headSha, IReadOnlyList<string> failingNames,
        CancellationToken cancellationToken = default)
    {
        if (failingNames.Count == 0)
        {
            return "";
        }
        // Get the MR's pipeline jobs, find a failed one whose name matches,
        // trace it. Every failure here is best-effort: return "" rather than
        // erroring.
        int pipelineId;
        try
        {
            pipelineId = await HeadPipelineIdAsync(pr, throwOnFailure: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ScmCommandException)
        {
            return "";
        }
        if (pipelineId == 0)
        {
            return "";
        }
        var jobsResult = await _run("glab", PipelineJobsArgs(pipelineId), null, cancellationToken)
            .ConfigureAwait(false);
        if (!jobsResult.Success)
        {
            return "";
        }
        var jobId = FindFailedJobId(jobsResult.CombinedOutput, failingNames);
        if (jobId == 0)
        {
            return "";
        }
        var traceResult = await _run(
            "glab", new[] { "ci", "trace", jobId.ToString(CultureInfo.InvariantCulture) },
            null, cancellationToken).ConfigureAwait(false);
        return traceResult.Stdout.Trim();
    }

    /// <summary>
    /// Reads the MR's head pipeline id via `glab mr view`. Returns 0 when the
    /// MR has no pipeline. When <paramref name="throwOnFailure"/> is false a
    /// failed or unparseable view also yields 0 (best-effort log fetching);
    /// when true it throws, mirroring the fallback checks path.
    /// </summary>
    private async Task<int> HeadPipelineIdAsync(
        PullRequest pr, bool throwOnFailure, CancellationToken cancellationToken)
    {
        var result = await _run(
            "glab", new[] { "mr", "view", pr.Number, "--output", "json" }, null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            if (throwOnFailure)
            {
                throw new ScmCommandException(
                    $"glab mr view: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
            }
            return 0;
        }
        var trimmed = TrimToJson(result.CombinedOutput);
        HeadPipelinePayload? payload = null;
        if (trimmed.Length > 0)
        {
            try
            {
                payload = JsonSerializer.Deserialize<HeadPipelinePayload>(trimmed);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }
        if (payload is null)
        {
            if (throwOnFailure)
            {
                throw new ScmCommandException(
                    $"glab mr view: invalid JSON output: {result.CombinedOutput.Trim()}");
            }
            return 0;
        }
        return payload.HeadPipeline?.Id ?? 0;
    }

    private static IReadOnlyList<Check> ChecksOrThrow(string output)
    {
        var (checks, error) = ParseGitlabJobs(output);
        if (error is not null)
        {
            // Surface a decode error even when some jobs parsed. A corrupt
            // later page of paginated `glab api` output must not let a
            // partial slice look authoritative: a failed job on the dropped
            // page would otherwise be hidden and the CI verdict would read
            // green.
            throw new ScmCommandException(error);
        }
        return checks;
    }

    internal sealed record MrPayload(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("web_url")] string? WebUrl,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("has_conflicts")] bool HasConflicts,
        [property: JsonPropertyName("detailed_merge_status")] string? DetailedMergeStatus,
        [property: JsonPropertyName("merge_status")] string? MergeStatus);

    private sealed record HeadPipelinePayload(
        [property: JsonPropertyName("head_pipeline")] HeadPipeline? HeadPipeline);

    private sealed record HeadPipeline([property: JsonPropertyName("id")] int Id);

    internal static MrPayload? ParseMRPayload(string output)
    {
        var trimmed = TrimToJson(output);
        if (trimmed.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<MrPayload>(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// glab may emit a banner line before JSON; skip until the first '{' or
    /// '['. Mirrors Go's <c>bytesTrimToJSON</c>.
    /// </summary>
    internal static string TrimToJson(string output)
    {
        var idx = output.IndexOfAny(new[] { '{', '[' });
        return idx < 0 ? "" : output[idx..];
    }

    internal sealed record GitlabJob(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("stage")] string? Stage,
        [property: JsonPropertyName("finished_at")] string? FinishedAt)
    {
        /// <summary>
        /// Parses the job's finished_at timestamp, returning null when it is
        /// absent or unparseable. GitLab emits RFC3339 (often with fractional
        /// seconds and a 'Z' offset).
        /// </summary>
        public DateTimeOffset? CompletedAt()
        {
            if (FinishedAt is null || FinishedAt.Trim().Length == 0)
            {
                return null;
            }
            if (DateTimeOffset.TryParse(
                FinishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
            return null;
        }
    }

    /// <summary>
    /// Reads every job from glab output. The output may contain a single bare
    /// job array, a pipeline object with nested .jobs, or - when `glab api
    /// --paginate` walks multiple pages - several JSON documents concatenated
    /// back to back (one array per page). Each top-level value is read in
    /// turn and the jobs accumulate across all of them. Returns whatever was
    /// parsed plus a non-null error when a document was malformed: a corrupt
    /// page must be surfaced by the caller instead of mistaken for an empty
    /// result.
    /// </summary>
    internal static (List<GitlabJob> Jobs, string? Error) DecodeGitlabJobs(string output)
    {
        var jobs = new List<GitlabJob>();
        var trimmed = TrimToJson(output);
        if (trimmed.Length == 0)
        {
            return (jobs, null);
        }
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var span = bytes.AsSpan(consumed);
            if (IsJsonWhitespaceOnly(span))
            {
                break;
            }
            var reader = new Utf8JsonReader(span, isFinalBlock: true, state: default);
            JsonDocument? doc;
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out doc))
                {
                    break;
                }
            }
            catch (JsonException e)
            {
                // Malformed mid-stream document: stop, but keep what parsed
                // so far and report the error rather than silently swallowing
                // the page.
                return (jobs, $"decode gitlab jobs: {e.Message}");
            }
            using (doc)
            {
                AppendJobs(doc.RootElement, jobs);
            }
            consumed += (int)reader.BytesConsumed;
        }
        return (jobs, null);
    }

    private static bool IsJsonWhitespaceOnly(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }
        return true;
    }

    private static void AppendJobs(JsonElement root, List<GitlabJob> jobs)
    {
        try
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                var asArray = root.Deserialize<List<GitlabJob>>();
                if (asArray is not null && asArray.Count > 0)
                {
                    jobs.AddRange(asArray);
                }
                return;
            }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("jobs", out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                var asObject = nested.Deserialize<List<GitlabJob>>();
                if (asObject is not null && asObject.Count > 0)
                {
                    jobs.AddRange(asObject);
                }
            }
        }
        catch (JsonException)
        {
            // A well-formed document whose shape doesn't match is skipped,
            // matching Go's unmarshal-and-ignore behavior.
        }
    }

    internal static (IReadOnlyList<Check> Checks, string? Error) ParseGitlabJobs(string output)
    {
        var (jobs, error) = DecodeGitlabJobs(output);
        if (jobs.Count == 0)
        {
            return (Array.Empty<Check>(), error);
        }
        var checks = new List<Check>(jobs.Count);
        foreach (var job in jobs)
        {
            checks.Add(new Check(job.Name ?? "", GitlabStatusBucket(job.Status ?? ""), job.CompletedAt()));
        }
        return (checks, error);
    }

    internal static int FindFailedJobId(string output, IReadOnlyList<string> failingNames)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in failingNames)
        {
            var trimmedName = name.Trim();
            if (trimmedName.Length > 0)
            {
                targets.Add(trimmedName);
            }
        }
        // Best effort: scan whatever jobs parsed; a corrupt later page does
        // not prevent locating a failed job that already decoded.
        var (jobs, _) = DecodeGitlabJobs(output);
        foreach (var job in jobs)
        {
            if (!string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (targets.Count == 0 || targets.Contains(job.Name ?? ""))
            {
                return job.Id;
            }
        }
        return 0;
    }

    internal static CheckBucket GitlabStatusBucket(string state)
        => state.Trim().ToLowerInvariant() switch
        {
            "success" => CheckBucket.Pass,
            "failed" => CheckBucket.Fail,
            "canceled" or "cancelled" => CheckBucket.Cancel,
            "skipped" => CheckBucket.Skipping,
            "manual" => CheckBucket.Skipping,
            "pending" or "running" or "created" or "waiting_for_resource" or "preparing" or "scheduled"
                => CheckBucket.Pending,
            _ => CheckBucket.None,
        };

    internal static PullRequestState NormalizePRState(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "opened" or "open" => PullRequestState.Open,
            "merged" => PullRequestState.Merged,
            "closed" or "locked" => PullRequestState.Closed,
            _ => PullRequestState.Unknown,
        };

    internal static string ExtractMRUrl(string raw)
    {
        var text = raw.Trim();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("http://", StringComparison.Ordinal)
                || line.StartsWith("https://", StringComparison.Ordinal))
            {
                return line;
            }
        }
        var trimmed = TrimToJson(raw);
        if (trimmed.Length == 0)
        {
            return "";
        }
        Dictionary<string, JsonElement>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(trimmed);
        }
        catch (JsonException)
        {
            return "";
        }
        if (payload is null)
        {
            return "";
        }
        foreach (var key in new[] { "web_url", "url", "webUrl" })
        {
            if (payload.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var url = (value.GetString() ?? "").Trim();
                if (url.Length > 0)
                {
                    return url;
                }
            }
        }
        return "";
    }
}
