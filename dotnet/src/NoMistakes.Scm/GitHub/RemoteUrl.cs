namespace NoMistakes.Scm.GitHub;

/// <summary>
/// GitHub remote-URL parsing, mirroring Go's
/// <c>internal/scm/github.RepoSlug</c>.
/// </summary>
public static class RemoteUrl
{
    /// <summary>
    /// Extracts the "owner/name" identifier from a GitHub remote or PR URL.
    /// Supports https URLs, scp-style ssh URLs (git@github.com:owner/name.git),
    /// ssh:// URLs, and longer paths such as PR links (the leading two path
    /// segments are used). Returns "" when the input has no owner/name pair.
    /// </summary>
    public static string RepoSlug(string remoteUrl)
    {
        var raw = remoteUrl.Trim();
        if (raw.Length == 0)
        {
            return "";
        }
        if (raw.EndsWith(".git", StringComparison.Ordinal))
        {
            raw = raw[..^4];
        }

        // Reduce raw to the path portion after the host.
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var rest = raw[(schemeEnd + 3)..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return "";
            }
            raw = rest[(slash + 1)..];
        }
        else
        {
            var colon = raw.IndexOf(':');
            if (colon >= 0)
            {
                // scp-style ssh: [user@]host:owner/name
                raw = raw[(colon + 1)..];
            }
        }

        var parts = raw.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            return "";
        }
        var owner = parts[0].Trim();
        var name = parts[1].Trim();
        if (owner.Length == 0 || name.Length == 0)
        {
            return "";
        }
        return owner + "/" + name;
    }

    /// <summary>
    /// Returns "host/owner/name" for GitHub Enterprise Server instances and
    /// plain "owner/name" for github.com. This is the format that the gh
    /// CLI's --repo flag requires for GHE. Mirrors Go's
    /// <c>github.HostPrefixedSlug</c>.
    /// </summary>
    public static string HostPrefixedSlug(string remoteUrl)
    {
        var slug = RepoSlug(remoteUrl);
        if (slug.Length == 0)
        {
            return "";
        }
        var host = RemoteHost.ExtractHost(remoteUrl);
        if (host.Length == 0 || string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return slug;
        }
        return host + "/" + slug;
    }
}
