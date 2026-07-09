using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Scm.AzureDevOps;

/// <summary>
/// Talks to Azure DevOps through the az CLI (azure-devops extension),
/// mirroring Go's <c>internal/scm/azuredevops.Host</c>. <paramref name="org"/>
/// is the organization URL (e.g. https://dev.azure.com/myorg); it is passed
/// via --organization to every command so they resolve the right organization
/// regardless of the process working directory (the daemon runs from a fixed,
/// non-repo working dir - without it az cannot infer the org or repo and
/// fails on every poll). <paramref name="project"/> and <paramref name="repo"/>
/// name the repository; the project name may contain spaces. All three are
/// optional; empty reproduces the unscoped behavior.
/// </summary>
public sealed class Host(CommandRunner run, Func<bool>? cliAvailable, string org, string project, string repo)
    : IHost
{
    private readonly CommandRunner _run = run;
    private readonly Func<bool>? _cliAvailable = cliAvailable;
    private readonly string _org = org.Trim();
    private readonly string _project = project.Trim();
    private readonly string _repo = repo.Trim();

    public Provider Provider => Provider.AzureDevOps;

    /// <summary>
    /// Merge status is reliably available from `az repos pr show`.
    /// Failed-check log fetching is not wired up - the az CLI has no
    /// first-class build-log command - so callers gate on FailedCheckLogs and
    /// skip it.
    /// </summary>
    public Capabilities Capabilities => new(MergeableState: true, FailedCheckLogs: false);

    /// <summary>
    /// Scopes a command to the organization. The show/update/policy-list
    /// commands accept only --organization because the PR id is
    /// organization-unique; passing --project/--repository to them is rejected
    /// by az.
    /// </summary>
    private List<string> OrgArgs()
    {
        var args = new List<string>();
        if (_org.Length > 0)
        {
            args.Add("--organization");
            args.Add(_org);
        }
        return args;
    }

    /// <summary>
    /// Fully scopes a command to org/project/repo. The create and list
    /// commands need all three to resolve the repository.
    /// </summary>
    private List<string> ScopeArgs()
    {
        var args = OrgArgs();
        if (_project.Length > 0)
        {
            args.Add("--project");
            args.Add(_project);
        }
        if (_repo.Length > 0)
        {
            args.Add("--repository");
            args.Add(_repo);
        }
        return args;
    }

    public async Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (_cliAvailable is not null && !_cliAvailable())
        {
            return "az CLI is not installed";
        }
        // The azure-devops extension is separate from the az binary; without
        // it every `az repos`/`az devops` command fails. Probe it for a clear
        // message.
        var extension = await _run(
            "az", new[] { "extension", "show", "--name", "azure-devops" }, null, cancellationToken)
            .ConfigureAwait(false);
        if (!extension.Success)
        {
            return "az azure-devops extension is not installed (run: az extension add --name azure-devops)";
        }
        // Auth probe: an organization-scoped read exercises the PAT
        // (AZURE_DEVOPS_EXT_PAT, or `az devops login`) against this
        // organization.
        var authArgs = new List<string>
        {
            "devops", "project", "list", "--query", "value[0].id", "--output", "tsv",
        };
        authArgs.AddRange(OrgArgs());
        var auth = await _run("az", authArgs, null, cancellationToken).ConfigureAwait(false);
        if (!auth.Success)
        {
            return "az CLI is not authenticated for Azure DevOps";
        }
        return null;
    }

    /// <summary>
    /// Returns the first active PR for the source branch, or null when none
    /// exists. Mirrors Go's <c>FindPR</c>.
    /// </summary>
    public async Task<PullRequest?> FindPRAsync(
        string branch, string baseBranch, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "repos", "pr", "list", "--source-branch", branch, "--status", "active",
        };
        if (baseBranch.Trim().Length > 0)
        {
            args.Add("--target-branch");
            args.Add(baseBranch);
        }
        args.AddRange(ScopeArgs());
        args.Add("--output");
        args.Add("json");
        var stdout = await OutputJsonAsync("az repos pr list", args, cancellationToken).ConfigureAwait(false);
        if (stdout.Trim().Length == 0)
        {
            return null;
        }
        List<AzPR>? prs;
        try
        {
            prs = JsonSerializer.Deserialize<List<AzPR>>(stdout);
        }
        catch (JsonException e)
        {
            throw new ScmCommandException($"az repos pr list: parse response: {e.Message}");
        }
        if (prs is null || prs.Count == 0)
        {
            return null;
        }
        return ToPR(prs[0]);
    }

    public async Task<PullRequest> CreatePRAsync(
        string branch, string baseBranch, PullRequestContent content,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "repos", "pr", "create",
            "--source-branch", branch,
            "--target-branch", baseBranch,
            "--title", content.Title,
            "--description", ClampDescription(content.Body),
        };
        args.AddRange(ScopeArgs());
        args.Add("--output");
        args.Add("json");
        var stdout = await OutputJsonAsync("az repos pr create", args, cancellationToken).ConfigureAwait(false);
        AzPR pr;
        try
        {
            pr = JsonSerializer.Deserialize<AzPR>(stdout)
                ?? throw new JsonException("null payload");
        }
        catch (JsonException e)
        {
            throw new ScmCommandException($"az repos pr create: parse response: {e.Message}");
        }
        return ToPR(pr);
    }

    public async Task<PullRequest> UpdatePRAsync(
        PullRequest pr, PullRequestContent content, CancellationToken cancellationToken = default)
    {
        var id = PRId(pr);
        if (id.Length == 0)
        {
            throw new ScmCommandException("az repos pr update: missing PR id");
        }
        var args = new List<string>
        {
            "repos", "pr", "update", "--id", id,
            "--title", content.Title,
            "--description", ClampDescription(content.Body),
        };
        args.AddRange(OrgArgs());
        args.Add("--output");
        args.Add("json");
        await OutputJsonAsync("az repos pr update", args, cancellationToken).ConfigureAwait(false);
        return pr;
    }

    public async Task<PullRequestState> GetPRStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var got = await ShowPRAsync(pr, cancellationToken).ConfigureAwait(false);
        return NormalizePRState(got.Status ?? "");
    }

    public async Task<IReadOnlyList<Check>> GetChecksAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var id = PRId(pr);
        if (id.Length == 0)
        {
            throw new ScmCommandException("az repos pr policy list: missing PR id");
        }
        var args = new List<string> { "repos", "pr", "policy", "list", "--id", id };
        args.AddRange(OrgArgs());
        args.Add("--output");
        args.Add("json");
        var stdout = await OutputJsonAsync("az repos pr policy list", args, cancellationToken).ConfigureAwait(false);
        List<PolicyEval>? evals;
        try
        {
            evals = JsonSerializer.Deserialize<List<PolicyEval>>(stdout);
        }
        catch (JsonException e)
        {
            throw new ScmCommandException($"parse policy evaluations: {e.Message}");
        }
        var checks = new List<Check>(evals?.Count ?? 0);
        foreach (var e in evals ?? new List<PolicyEval>())
        {
            if (!e.IsCICheck())
            {
                continue;
            }
            var bucket = StatusBucket(e.Status ?? "");
            if (bucket == CheckBucket.None)
            {
                continue;
            }
            checks.Add(new Check(e.CheckName(), bucket, ParseAzTime(e.CompletedDate)));
        }
        return checks;
    }

    public async Task<MergeableState> GetMergeableStateAsync(
        PullRequest pr, CancellationToken cancellationToken = default)
    {
        var got = await ShowPRAsync(pr, cancellationToken).ConfigureAwait(false);
        return NormalizeMergeableState(got.MergeStatus ?? "");
    }

    /// <summary>
    /// Not implemented for Azure DevOps; callers gate on
    /// Capabilities.FailedCheckLogs (false) and skip it. Mirrors Go returning
    /// <c>scm.ErrUnsupported</c>.
    /// </summary>
    public Task<string> FetchFailedCheckLogsAsync(
        PullRequest pr, string branch, string headSha, IReadOnlyList<string> failingNames,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("operation not supported by this provider");

    /// <summary>
    /// Runs an az command and returns its stdout alone, leaving stderr out of
    /// the payload so non-JSON az chatter (preview-command notices,
    /// token-refresh messages) cannot corrupt the bytes a caller deserializes.
    /// On failure it surfaces the separately-captured stderr in the error.
    /// Mirrors Go's <c>outputJSON</c>.
    /// </summary>
    private async Task<string> OutputJsonAsync(
        string context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await _run("az", args, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var stderr = result.Stderr.Trim();
            throw new ScmCommandException(stderr.Length > 0
                ? $"{context}: {stderr}: exit status {result.ExitCode}"
                : $"{context}: exit status {result.ExitCode}");
        }
        return result.Stdout;
    }

    private async Task<AzPR> ShowPRAsync(PullRequest pr, CancellationToken cancellationToken)
    {
        var id = PRId(pr);
        if (id.Length == 0)
        {
            throw new ScmCommandException("az repos pr show: missing PR id");
        }
        var args = new List<string> { "repos", "pr", "show", "--id", id };
        args.AddRange(OrgArgs());
        args.Add("--output");
        args.Add("json");
        var stdout = await OutputJsonAsync("az repos pr show", args, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<AzPR>(stdout)
                ?? throw new JsonException("null payload");
        }
        catch (JsonException e)
        {
            throw new ScmCommandException($"parse pull request: {e.Message}");
        }
    }

    private static string PRId(PullRequest pr)
    {
        var id = pr.Number.Trim();
        if (id.Length > 0)
        {
            return id;
        }
        return PullRequestUrl.TryExtractNumber(pr.Url, out var number) ? number : "";
    }

    private PullRequest ToPR(AzPR raw)
    {
        var id = raw.PullRequestId > 0
            ? raw.PullRequestId.ToString(CultureInfo.InvariantCulture)
            : "";
        return new PullRequest
        {
            Number = id,
            Url = RemoteUrl.WebPRUrl(_org, _project, _repo, raw.Repository?.WebUrl ?? "", id),
        };
    }

    private string ClampDescription(string body) => PRBody.Clamp(body, PRBody.MaxChars(Provider.AzureDevOps));

    internal static PullRequestState NormalizePRState(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "active" => PullRequestState.Open,
            "completed" => PullRequestState.Merged,
            "abandoned" => PullRequestState.Closed,
            _ => PullRequestState.Unknown,
        };

    internal static MergeableState NormalizeMergeableState(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "succeeded" => MergeableState.Mergeable,
            "conflicts" => MergeableState.Conflicting,
            // notSet, queued, rejectedByPolicy, failure, and unknown statuses
            // are not git merge conflicts: rejectedByPolicy means branch
            // policies are unsatisfied (surfaced separately as checks), and
            // failure is a generic often-transient async merge computation
            // result. Treating them as pending avoids driving the CI auto-fix
            // loop into pointless rebases.
            _ => MergeableState.Pending,
        };

    internal static CheckBucket StatusBucket(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            "approved" => CheckBucket.Pass,
            "rejected" or "broken" => CheckBucket.Fail,
            "queued" or "running" => CheckBucket.Pending,
            // notApplicable and unknown statuses are omitted so they never
            // gate CI.
            _ => CheckBucket.None,
        };

    private static DateTimeOffset? ParseAzTime(string? raw)
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

    /// <summary>
    /// The subset of `az repos pr show/create/update` JSON output consumed.
    /// </summary>
    internal sealed record AzPR(
        [property: JsonPropertyName("pullRequestId")] int PullRequestId,
        // active | completed | abandoned
        [property: JsonPropertyName("status")] string? Status,
        // notSet | queued | conflicts | succeeded | rejectedByPolicy | failure
        [property: JsonPropertyName("mergeStatus")] string? MergeStatus,
        [property: JsonPropertyName("repository")] AzRepository? Repository);

    internal sealed record AzRepository(
        // .../_git/{repo} - browsable base (the PR's top-level "url" field is
        // an _apis/... endpoint, NOT browsable)
        [property: JsonPropertyName("webUrl")] string? WebUrl);

    /// <summary>
    /// The subset of `az repos pr policy list` evaluation records consumed.
    /// Branch policy evaluations are Azure DevOps's equivalent of PR checks.
    /// </summary>
    internal sealed record PolicyEval(
        // queued | running | approved | rejected | notApplicable | broken
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("completedDate")] string? CompletedDate,
        [property: JsonPropertyName("configuration")] PolicyConfiguration? Configuration,
        [property: JsonPropertyName("context")] Dictionary<string, JsonElement>? Context)
    {
        /// <summary>
        /// Reports whether a policy evaluation represents an automated check
        /// the CI monitor can meaningfully gate on and auto-fix, as opposed
        /// to a human/merge gate. Azure DevOps's automated policy types are
        /// "Build" (build validation) and "Status" (external status checks).
        /// Approval-gating policies (Minimum number of reviewers, Required
        /// reviewers, Comment requirements, Work item linking) and
        /// merge-config policies (Require a merge strategy) report a
        /// blocking/rejected status on a normal open PR that is simply
        /// awaiting human review; surfacing them as failing checks would
        /// drive the CI auto-fix loop into pointless attempts it can never
        /// satisfy. They are excluded here.
        /// </summary>
        public bool IsCICheck()
            => (Configuration?.Type?.DisplayName ?? "").Trim().ToLowerInvariant() is "build" or "status";

        /// <summary>
        /// Derives a human-readable check name, preferring the policy's
        /// configured display name, then the triggered build definition name,
        /// then the policy type.
        /// </summary>
        public string CheckName()
        {
            var name = (Configuration?.Settings?.DisplayName ?? "").Trim();
            if (name.Length > 0)
            {
                return name;
            }
            if (Context is not null
                && Context.TryGetValue("buildDefinitionName", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                var fromContext = (v.GetString() ?? "").Trim();
                if (fromContext.Length > 0)
                {
                    return fromContext;
                }
            }
            var typeName = (Configuration?.Type?.DisplayName ?? "").Trim();
            return typeName.Length > 0 ? typeName : "policy";
        }
    }

    internal sealed record PolicyConfiguration(
        [property: JsonPropertyName("type")] PolicyDisplayName? Type,
        [property: JsonPropertyName("settings")] PolicyDisplayName? Settings);

    internal sealed record PolicyDisplayName(
        [property: JsonPropertyName("displayName")] string? DisplayName);
}
