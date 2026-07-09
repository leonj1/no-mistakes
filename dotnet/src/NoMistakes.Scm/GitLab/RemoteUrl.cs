namespace NoMistakes.Scm.GitLab;

/// <summary>
/// GitLab remote-URL parsing, mirroring Go's
/// <c>internal/scm/gitlab.ProjectPath</c>.
/// </summary>
public static class RemoteUrl
{
    /// <summary>
    /// Extracts the "group/project" path (no host, no trailing .git) from a
    /// GitLab remote URL. GitLab projects can live under nested subgroups, so
    /// the full path - not just the last two segments - is returned. Handles
    /// HTTPS/ssh:// URLs and scp-style SSH (git@host:group/project.git).
    /// Returns "" when no path can be determined; callers treat that as
    /// "unknown" and fall back to branch-dependent porcelain.
    /// </summary>
    public static string ProjectPath(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return "";
        }
        var path = "";
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                path = Uri.UnescapeDataString(uri.AbsolutePath);
            }
        }
        else
        {
            var colon = raw.IndexOf(':');
            if (colon >= 0 && !IsWindowsDrivePath(raw))
            {
                // scp-style: [user@]host:group/project.git -> group/project. The
                // first ':' separates host from path, so the path is recovered
                // whether or not a "user@" prefix is present (e.g.
                // gitlab.example.com:group/project.git). A Windows drive-letter
                // path (C:\...) carries a colon too, but it is a local
                // filesystem path, not a remote URL, so it is excluded above.
                path = raw[(colon + 1)..];
            }
        }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal))
        {
            path = path[..^4];
        }
        return path;
    }

    /// <summary>
    /// Reports whether raw begins with a Windows drive specifier like
    /// "C:\..." or "C:/...". Such a path's drive-letter colon must not be
    /// mistaken for the host:path separator of scp-style SSH syntax, which
    /// would otherwise turn a local filesystem path into a spurious
    /// "group/project".
    /// </summary>
    private static bool IsWindowsDrivePath(string raw)
    {
        if (raw.Length < 2 || raw[1] != ':')
        {
            return false;
        }
        var c = raw[0];
        if (!char.IsAsciiLetter(c))
        {
            return false;
        }
        return raw.Length == 2 || raw[2] == '\\' || raw[2] == '/';
    }
}
