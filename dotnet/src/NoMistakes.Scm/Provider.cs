namespace NoMistakes.Scm;

/// <summary>
/// A supported SCM hosting provider. Mirrors Go's <c>internal/scm.Provider</c>
/// string constants (github, gitlab, bitbucket, azuredevops, unknown).
/// </summary>
public enum Provider
{
    Unknown,
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps,
}
