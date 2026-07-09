namespace NoMistakes.Scm;

/// <summary>
/// Decides whether a repo's configured fork push target may participate in PR
/// creation, porting the fork-routing guards from Go's pipeline buildHost
/// (internal/pipeline/steps/host.go). GitHub is the only provider with
/// end-to-end fork PR routing (gh pr create --head owner:branch against the
/// parent repo); on every other provider the push step may use the fork URL,
/// but PR creation must fail closed - a legacy or manually edited fork_url
/// must skip with a reason instead of opening a self PR on the fork.
/// </summary>
public static class ForkRouting
{
    /// <summary>
    /// Returns null when PR creation may proceed (no fork configured, or the
    /// provider supports fork routing end to end), otherwise a human-readable
    /// skip reason suitable for logging.
    /// </summary>
    public static string? SkipReason(Provider provider, string forkUrl)
    {
        if (forkUrl.Trim().Length == 0)
        {
            return null;
        }
        return provider switch
        {
            Provider.GitHub => null,
            Provider.GitLab => "fork PR routing for GitLab is not implemented",
            Provider.Bitbucket => "fork PR routing for Bitbucket is not implemented",
            Provider.AzureDevOps => "fork PR routing for Azure DevOps is not implemented",
            // An unrecognized provider cannot have fork routing; fail closed.
            _ => $"fork PR routing for {provider} is not implemented",
        };
    }
}
