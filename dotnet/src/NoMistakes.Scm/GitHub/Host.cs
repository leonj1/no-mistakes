using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Scm.GitHub;

/// <summary>
/// Talks to GitHub through the gh CLI, mirroring Go's
/// <c>internal/scm/github.Host</c>. The runner reports whether the gh binary
/// resolved on PATH via <paramref name="cliAvailable"/>; <paramref name="host"/>
/// is the repo's GitHub hostname, and when set the availability check is
/// scoped to it via --hostname so a stale credential for an unrelated
/// configured gh host cannot make this repo look unauthenticated.
/// <paramref name="repo"/> is the "owner/name" slug; when set it is passed via
/// --repo to every PR/run command so they resolve the right repository
/// regardless of the process working directory (the daemon runs from a fixed,
/// non-repo working dir). Both are optional; empty reproduces the legacy
/// unscoped behavior. <paramref name="forkRepo"/> is the contributor fork's
/// "owner/name" slug (Go's <c>NewWithFork</c>); only its owner is kept,
/// because gh pr create expects --head &lt;owner&gt;:&lt;branch&gt; for
/// cross-repository PR heads.
/// </summary>
public sealed class Host(CommandRunner run, Func<bool>? cliAvailable, string host, string repo, string forkRepo)
    : IHost
{
    public Host(CommandRunner run, Func<bool>? cliAvailable, string host, string repo)
        : this(run, cliAvailable, host, repo, "")
    {
    }

    private readonly CommandRunner _run = run;
    private readonly Func<bool>? _cliAvailable = cliAvailable;
    private readonly string _host = host.Trim();
    private readonly string _repo = repo.Trim();
    private readonly string _forkOwner = RepoOwner(forkRepo);

    public Provider Provider => Provider.GitHub;

    public Capabilities Capabilities => new(MergeableState: true, FailedCheckLogs: true);

    /// <summary>
    /// Returns the --repo flag pair when the slug is known, so gh commands
    /// resolve the right repository regardless of the process working
    /// directory.
    /// </summary>
    private string[] RepoArgs()
        => _repo.Length == 0 ? Array.Empty<string>() : new[] { "--repo", _repo };

    /// <summary>
    /// The --head value for pr create: owner-prefixed when a fork is
    /// configured (cross-repository head), bare otherwise. pr list must NOT
    /// use this - see <see cref="FindPRAsync"/>.
    /// </summary>
    private string HeadRef(string branch)
        => _forkOwner.Length == 0 ? branch : _forkOwner + ":" + branch;

    private static string RepoOwner(string slug)
    {
        var trimmed = slug.Trim();
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? "" : trimmed[..slash].Trim();
    }

    public async Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (_cliAvailable is not null && !_cliAvailable())
        {
            return "gh CLI is not installed";
        }
        // Scope the auth check to this repo's host. Unscoped `gh auth status`
        // checks every authenticated account and exits non-zero if ANY of them
        // has a stale/expired token, even when this repo's own host is fully
        // authenticated. Passing --hostname keeps an unrelated bad credential
        // from poisoning availability for this repo. When the host is unknown
        // we fall back to the unscoped check (fail-safe: same behavior as
        // before).
        var authArgs = new List<string> { "auth", "status" };
        if (_host.Length > 0)
        {
            authArgs.Add("--hostname");
            authArgs.Add(_host);
        }
        var result = await _run("gh", authArgs, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return "gh CLI is not authenticated";
        }
        return null;
    }

    /// <summary>
    /// Returns the open PR for the source branch, or null when none exists.
    /// gh pr list --head does not accept the "&lt;owner&gt;:&lt;branch&gt;"
    /// form pr create uses, so the fork lookup lists by the bare branch name
    /// and filters the returned headRefName/headRepositoryOwner fields down
    /// to the configured fork owner. Mirrors Go's <c>FindPR</c>.
    /// </summary>
    public async Task<PullRequest?> FindPRAsync(
        string branch, string baseBranch, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pr", "list", "--head", branch };
        if (baseBranch.Trim().Length > 0)
        {
            args.Add("--base");
            args.Add(baseBranch);
        }
        args.AddRange(RepoArgs());
        var jsonFields = _forkOwner.Length == 0
            ? "number,url"
            : "number,url,headRefName,headRepositoryOwner";
        args.AddRange(new[] { "--state", "open", "--json", jsonFields });
        var result = await _run("gh", args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"gh pr list: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        List<PrListEntry>? prs;
        try
        {
            prs = JsonSerializer.Deserialize<List<PrListEntry>>(result.CombinedOutput);
        }
        catch (JsonException)
        {
            // Go returns (nil, nil) when the list output does not unmarshal.
            return null;
        }
        if (prs is null || prs.Count == 0)
        {
            return null;
        }
        foreach (var candidate in prs)
        {
            if (!MatchesHead(candidate.HeadRefName, candidate.HeadRepositoryOwner, branch))
            {
                continue;
            }
            var url = (candidate.Url ?? "").Trim();
            if (url.Length == 0)
            {
                return null;
            }
            var number = candidate.Number > 0
                ? candidate.Number.ToString(CultureInfo.InvariantCulture)
                : PullRequestUrl.TryExtractNumber(url, out var fromUrl) ? fromUrl : "";
            return new PullRequest { Number = number, Url = url };
        }
        return null;
    }

    /// <summary>
    /// Filters a listed PR down to the configured fork owner. Without a fork
    /// every candidate matches (the bare-branch list is already scoped).
    /// </summary>
    private bool MatchesHead(string? headRefName, PrListOwner? owner, string branch)
    {
        if (_forkOwner.Length == 0)
        {
            return true;
        }
        var head = (headRefName ?? "").Trim();
        if (head.Length > 0 && headRefName != branch)
        {
            return false;
        }
        if (owner is null)
        {
            return false;
        }
        return string.Equals((owner.Login ?? "").Trim(), _forkOwner, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PullRequest> CreatePRAsync(
        string branch, string baseBranch, PullRequestContent content,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pr", "create", "--head", HeadRef(branch), "--base", baseBranch };
        args.AddRange(RepoArgs());
        args.AddRange(new[] { "--title", content.Title, "--body-file", "-" });
        var result = await _run("gh", args, content.Body, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"gh pr create: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        var url = result.CombinedOutput.Trim();
        return new PullRequest
        {
            Number = PullRequestUrl.TryExtractNumber(url, out var number) ? number : "",
            Url = url,
        };
    }

    public async Task<PullRequest> UpdatePRAsync(
        PullRequest pr, PullRequestContent content, CancellationToken cancellationToken = default)
    {
        var id = pr.Number.Length > 0 ? pr.Number : pr.Url;
        var args = new List<string> { "pr", "edit", id };
        args.AddRange(RepoArgs());
        args.AddRange(new[] { "--title", content.Title, "--body-file", "-" });
        var result = await _run("gh", args, content.Body, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException(
                $"gh pr edit: {result.CombinedOutput.Trim()}: exit status {result.ExitCode}");
        }
        return pr;
    }

    public async Task<PullRequestState> GetPRStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pr", "view", pr.Number };
        args.AddRange(RepoArgs());
        args.AddRange(new[] { "--json", "state", "--jq", ".state" });
        var result = await _run("gh", args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException($"gh pr view: exit status {result.ExitCode}");
        }
        return NormalizePRState(result.Stdout.Trim());
    }

    public async Task<IReadOnlyList<Check>> GetChecksAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pr", "checks", pr.Number };
        args.AddRange(RepoArgs());
        args.AddRange(new[] { "--json", "name,state,bucket,completedAt" });
        var result = await _run("gh", args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.CombinedOutput.Contains("no checks reported", StringComparison.Ordinal))
            {
                return Array.Empty<Check>();
            }
            throw new ScmCommandException($"gh pr checks: exit status {result.ExitCode}");
        }
        List<RawCheck>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawCheck>>(result.CombinedOutput);
        }
        catch (JsonException e)
        {
            throw new ScmCommandException($"parse CI checks: {e.Message}");
        }
        if (raw is null)
        {
            return Array.Empty<Check>();
        }
        var checks = new List<Check>(raw.Count);
        foreach (var r in raw)
        {
            checks.Add(new Check(
                r.Name ?? "",
                NormalizeCheckBucket(r.Bucket, r.State),
                ParseTimestamp(r.CompletedAt)));
        }
        return checks;
    }

    public async Task<MergeableState> GetMergeableStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pr", "view", pr.Number };
        args.AddRange(RepoArgs());
        args.AddRange(new[] { "--json", "mergeable", "--jq", ".mergeable" });
        var result = await _run("gh", args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ScmCommandException($"gh pr view mergeable: exit status {result.ExitCode}");
        }
        return NormalizeMergeableState(result.Stdout.Trim());
    }

    public async Task<string> FetchFailedCheckLogsAsync(
        PullRequest pr, string branch, string headSha, IReadOnlyList<string> failingNames,
        CancellationToken cancellationToken = default)
    {
        if (failingNames.Count == 0)
        {
            return "";
        }
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in failingNames)
        {
            var normalized = NormalizeRunName(name);
            if (normalized.Length > 0)
            {
                targets.Add(normalized);
            }
        }
        if (targets.Count == 0)
        {
            return "";
        }
        var args = new List<string> { "run", "list", "--branch", branch };
        if (headSha.Trim().Length > 0)
        {
            args.Add("--commit");
            args.Add(headSha.Trim());
        }
        args.AddRange(RepoArgs());
        args.AddRange(new[]
        {
            "--status", "failure",
            "--limit", "20",
            "--json", "databaseId,headSha,name,displayTitle,workflowName",
        });
        var listResult = await _run("gh", args, null, cancellationToken).ConfigureAwait(false);
        if (!listResult.Success)
        {
            return "";
        }
        List<GitHubRun>? runs;
        try
        {
            runs = JsonSerializer.Deserialize<List<GitHubRun>>(listResult.Stdout);
        }
        catch (JsonException)
        {
            return "";
        }
        if (runs is null)
        {
            return "";
        }
        foreach (var run in runs)
        {
            if (!await RunMatchesTargetsAsync(run, targets, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            var viewArgs = new List<string>
            {
                "run", "view", run.DatabaseId.ToString(CultureInfo.InvariantCulture),
            };
            viewArgs.AddRange(RepoArgs());
            viewArgs.Add("--log-failed");
            var viewResult = await _run("gh", viewArgs, null, cancellationToken).ConfigureAwait(false);
            if (!viewResult.Success)
            {
                continue;
            }
            var logs = viewResult.Stdout.Trim();
            if (logs.Length > 0)
            {
                return logs;
            }
        }
        return "";
    }

    private async Task<bool> RunMatchesTargetsAsync(
        GitHubRun run, HashSet<string> targets, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { run.Name, run.DisplayTitle, run.WorkflowName })
        {
            if (targets.Contains(NormalizeRunName(candidate ?? "")))
            {
                return true;
            }
        }
        if (run.DatabaseId == 0)
        {
            return false;
        }
        var viewArgs = new List<string>
        {
            "run", "view", run.DatabaseId.ToString(CultureInfo.InvariantCulture),
        };
        viewArgs.AddRange(RepoArgs());
        viewArgs.AddRange(new[] { "--json", "jobs" });
        var result = await _run("gh", viewArgs, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return false;
        }
        GitHubRunView? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubRunView>(result.Stdout);
        }
        catch (JsonException)
        {
            return false;
        }
        if (payload?.Jobs is null)
        {
            return false;
        }
        foreach (var job in payload.Jobs)
        {
            if (!IsFailedJob(job))
            {
                continue;
            }
            if (targets.Contains(NormalizeRunName(job.Name ?? "")))
            {
                return true;
            }
        }
        return false;
    }

    private sealed record PrListEntry(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("headRefName")] string? HeadRefName,
        [property: JsonPropertyName("headRepositoryOwner")] PrListOwner? HeadRepositoryOwner);

    private sealed record PrListOwner([property: JsonPropertyName("login")] string? Login);

    private sealed record RawCheck(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("bucket")] string? Bucket,
        [property: JsonPropertyName("completedAt")] string? CompletedAt);

    private sealed record GitHubRun(
        [property: JsonPropertyName("databaseId")] int DatabaseId,
        [property: JsonPropertyName("headSha")] string? HeadSha,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("displayTitle")] string? DisplayTitle,
        [property: JsonPropertyName("workflowName")] string? WorkflowName);

    private sealed record GitHubRunView(
        [property: JsonPropertyName("jobs")] List<GitHubRunJob>? Jobs);

    private sealed record GitHubRunJob(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("conclusion")] string? Conclusion,
        [property: JsonPropertyName("status")] string? Status);

    private static bool IsFailedJob(GitHubRunJob job)
    {
        var state = (job.Conclusion ?? "").Trim().ToUpperInvariant();
        if (state.Length == 0)
        {
            state = (job.Status ?? "").Trim().ToUpperInvariant();
        }
        return state is "FAILURE" or "FAILED" or "ERROR" or "TIMED_OUT"
            or "ACTION_REQUIRED" or "STARTUP_FAILURE";
    }

    private static string NormalizeRunName(string name) => name.Trim().ToLowerInvariant();

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (raw is null || raw.Trim().Length == 0)
        {
            return null;
        }
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    internal static PullRequestState NormalizePRState(string raw)
        => raw.Trim().ToUpperInvariant() switch
        {
            "OPEN" => PullRequestState.Open,
            "MERGED" => PullRequestState.Merged,
            "CLOSED" => PullRequestState.Closed,
            _ => PullRequestState.Unknown,
        };

    internal static MergeableState NormalizeMergeableState(string raw)
        => raw.Trim().ToUpperInvariant() switch
        {
            "MERGEABLE" => MergeableState.Mergeable,
            "CONFLICTING" => MergeableState.Conflicting,
            "UNKNOWN" or "" => MergeableState.Pending,
            _ => MergeableState.Unknown,
        };

    internal static CheckBucket NormalizeCheckBucket(string? bucket, string? state)
    {
        var normalizedBucket = (bucket ?? "").Trim();
        if (normalizedBucket.Length > 0)
        {
            // Go casts a non-empty bucket straight through; an unrecognized
            // value maps to None (neither failing nor pending), matching the
            // unmatched-string behavior downstream.
            return normalizedBucket.ToLowerInvariant() switch
            {
                "pass" => CheckBucket.Pass,
                "fail" => CheckBucket.Fail,
                "pending" => CheckBucket.Pending,
                "cancel" => CheckBucket.Cancel,
                "skipping" => CheckBucket.Skipping,
                _ => CheckBucket.None,
            };
        }
        return (state ?? "").Trim().ToUpperInvariant() switch
        {
            "SUCCESS" => CheckBucket.Pass,
            "FAILURE" or "ERROR" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE"
                => CheckBucket.Fail,
            "PENDING" or "QUEUED" or "IN_PROGRESS" or "WAITING" or "REQUESTED" or "EXPECTED"
                => CheckBucket.Pending,
            "CANCELLED" => CheckBucket.Cancel,
            "SKIPPED" or "NEUTRAL" or "STALE" => CheckBucket.Skipping,
            _ => CheckBucket.None,
        };
    }
}
