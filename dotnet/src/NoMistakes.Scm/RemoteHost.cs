namespace NoMistakes.Scm;

/// <summary>
/// Host extraction from git remote URLs, mirroring Go's
/// <c>internal/scm.ExtractHost</c>.
/// </summary>
public static class RemoteHost
{
    /// <summary>
    /// Returns the lowercased host (without any port) from a git remote URL.
    /// Handles both scp-like syntax (git@host:group/project) and URL forms
    /// (https://host/group/project, ssh://git@host:22/group/project). Returns
    /// "" when no host can be determined.
    /// </summary>
    public static string ExtractHost(string remote)
    {
        var s = remote.Trim();
        if (s.Length == 0)
        {
            return "";
        }
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            // URL form: scheme://[user@]host[:port]/path. Split off the path at
            // the first '/' before scanning for userinfo, so a '@' inside the
            // path (e.g. .../group@prod/repo.git) cannot be mistaken for a
            // "user@" prefix.
            s = s[(schemeEnd + 3)..];
            var slash = s.IndexOf('/');
            if (slash >= 0)
            {
                s = s[..slash];
            }
            var at = s.LastIndexOf('@');
            if (at >= 0)
            {
                s = s[(at + 1)..];
            }
            return StripPort(s).ToLowerInvariant();
        }
        // No scheme. scp-like syntax is [user@]host:path; the first ':'
        // separates the host from the path. Split off the path first, then
        // strip any userinfo prefix from the host segment only, so a '@' in
        // the path (e.g. git@host:group@prod/repo.git) cannot collapse host
        // extraction.
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            s = s[..colon];
        }
        else
        {
            var slash = s.IndexOf('/');
            if (slash >= 0)
            {
                s = s[..slash];
            }
        }
        var hostAt = s.LastIndexOf('@');
        if (hostAt >= 0)
        {
            s = s[(hostAt + 1)..];
        }
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Removes a trailing :port from a host, leaving bare hosts and bracketed
    /// IPv6 literals intact.
    /// </summary>
    private static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            // IPv6 literal: [::1]:22 -> [::1]
            var end = host.IndexOf(']');
            return end >= 0 ? host[..(end + 1)] : host;
        }
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            var port = host[(colon + 1)..];
            if (port.Length > 0 && port.All(char.IsAsciiDigit))
            {
                return host[..colon];
            }
        }
        return host;
    }
}
