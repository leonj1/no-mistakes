namespace NoMistakes.Scm;

/// <summary>
/// Provider-agnostic interface to a PR-hosting service, mirroring Go's
/// <c>internal/scm.Host</c>. Transport (CLI vs HTTP API) is an implementation
/// detail. PR/MR lookup (Go's <c>FindPR</c>) joins in slice 6c alongside fork
/// routing.
///
/// Optional methods (<see cref="GetMergeableStateAsync"/>,
/// <see cref="FetchFailedCheckLogsAsync"/>) are gated on
/// <see cref="Capabilities"/>; implementations without the capability throw
/// <see cref="NotSupportedException"/> (Go's <c>ErrUnsupported</c>) as a
/// fallback, but callers should consult Capabilities rather than catch it.
/// </summary>
public interface IHost
{
    Provider Provider { get; }

    Capabilities Capabilities { get; }

    /// <summary>
    /// Returns null when the host is ready to use, or a descriptive reason
    /// why it is not (missing CLI, unauthenticated, etc). Mirrors Go's
    /// <c>Available</c> returning an error.
    /// </summary>
    Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<PullRequest> CreatePRAsync(
        string branch, string baseBranch, PullRequestContent content,
        CancellationToken cancellationToken = default);

    Task<PullRequest> UpdatePRAsync(
        PullRequest pr, PullRequestContent content,
        CancellationToken cancellationToken = default);

    Task<PullRequestState> GetPRStateAsync(PullRequest pr, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Check>> GetChecksAsync(PullRequest pr, CancellationToken cancellationToken = default);

    Task<MergeableState> GetMergeableStateAsync(PullRequest pr, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns "" when no logs can be retrieved.
    /// </summary>
    Task<string> FetchFailedCheckLogsAsync(
        PullRequest pr, string branch, string headSha, IReadOnlyList<string> failingNames,
        CancellationToken cancellationToken = default);
}
