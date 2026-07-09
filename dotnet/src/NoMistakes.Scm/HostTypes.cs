namespace NoMistakes.Scm;

/// <summary>
/// Identifies a pull/merge request on a provider. Mirrors Go's
/// <c>internal/scm.PR</c>.
/// </summary>
public sealed record PullRequest
{
    /// <summary>Provider-native identifier ("42"); "" when unknown.</summary>
    public string Number { get; init; } = "";

    /// <summary>Browsable web URL.</summary>
    public string Url { get; init; } = "";
}

/// <summary>
/// The title + body for creating or updating a PR. Mirrors Go's
/// <c>internal/scm.PRContent</c>.
/// </summary>
public sealed record PullRequestContent(string Title, string Body);

/// <summary>
/// Normalized lifecycle state of a PR. <see cref="Unknown"/> stands in for
/// Go's raw-string passthrough on unrecognized provider states: it matches no
/// terminal state, so callers keep polling.
/// </summary>
public enum PullRequestState
{
    Unknown,
    Open,
    Merged,
    Closed,
}

/// <summary>
/// Normalized merge-conflict status of a PR. Mirrors Go's
/// <c>internal/scm.MergeableState</c>.
/// </summary>
public enum MergeableState
{
    Unknown,
    Mergeable,
    Conflicting,
    Pending,
}

public static class MergeableStateExtensions
{
    /// <summary>Reports whether the state indicates a known merge conflict.</summary>
    public static bool IsConflict(this MergeableState s) => s == MergeableState.Conflicting;

    /// <summary>Reports whether the state is final (Mergeable or Conflicting).</summary>
    public static bool IsResolved(this MergeableState s)
        => s is MergeableState.Mergeable or MergeableState.Conflicting;
}

/// <summary>
/// Normalized outcome of a CI check. <see cref="None"/> is Go's empty-string
/// bucket (unrecognized provider status): neither failing nor pending.
/// </summary>
public enum CheckBucket
{
    None,
    Pass,
    Fail,
    Pending,
    Cancel,
    Skipping,
}

/// <summary>
/// A single CI check result on a PR. Mirrors Go's <c>internal/scm.Check</c>;
/// <see cref="CompletedAt"/> is null when unknown (Go's zero time) and is
/// used to detect CI re-runs between polls.
/// </summary>
public sealed record Check(string Name, CheckBucket Bucket, DateTimeOffset? CompletedAt = null)
{
    /// <summary>Reports whether the check is in a failed bucket.</summary>
    public bool IsFailing => Bucket == CheckBucket.Fail;

    /// <summary>Reports whether the check is still running or queued.</summary>
    public bool IsPending => Bucket == CheckBucket.Pending;
}

/// <summary>
/// Declares which optional host methods return meaningful data. Callers must
/// consult Capabilities before invoking optional methods.
/// </summary>
public readonly record struct Capabilities(bool MergeableState, bool FailedCheckLogs);

/// <summary>
/// PR/MR URL helpers shared across providers.
/// </summary>
public static class PullRequestUrl
{
    /// <summary>
    /// Extracts the trailing numeric segment from a PR/MR URL. Supports
    /// GitHub (/pull/N), GitLab (/-/merge_requests/N), Bitbucket
    /// (/pull-requests/N), and Azure DevOps (/pullrequest/N) URLs; all of
    /// them end in a digit path segment. Mirrors Go's
    /// <c>internal/scm.ExtractPRNumber</c>, with false standing in for the
    /// error return.
    /// </summary>
    public static bool TryExtractNumber(string prUrl, out string number)
    {
        number = "";
        var trimmed = prUrl.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var last = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (last.Length == 0 || !last.All(char.IsAsciiDigit))
        {
            return false;
        }
        number = last;
        return true;
    }
}
